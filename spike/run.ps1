# SPIKE (not for merging): local S3/SFTP test servers + measurements.
#   .\spike\run.ps1 up        start containers (random credentials, written to $env:TEMP\disktree-spike\, never committed)
#   .\spike\run.ps1 bench     run all measurements (optionally -DelayMs 20 adds netem latency to the containers)
#   .\spike\run.ps1 down      remove containers and test data
param([Parameter(Mandatory)][ValidateSet('up','bench','down')][string]$Phase, [int]$DelayMs = 0)
$ErrorActionPreference = 'Stop'
$work = Join-Path $env:TEMP 'disktree-spike'
$bench = Join-Path $PSScriptRoot 'Bench\Bench.csproj'
$containers = 'spike-rustfs','spike-seaweed','spike-sftp'

function Rand([int]$n) { -join ((48..57) + (65..90) + (97..122) | Get-Random -Count $n | ForEach-Object { [char]$_ }) }

if ($Phase -eq 'up') {
    New-Item -ItemType Directory -Force $work, "$work\keys" | Out-Null
    $s = @{ key = Rand 20; secret = Rand 40; pw = Rand 24 }
    $s | ConvertTo-Json | Set-Content "$work\secrets.json" -Encoding utf8

    # S3: RustFS (Apache-2.0) and SeaweedFS (Apache-2.0). minio/minio is no longer pullable from Docker Hub.
    docker run -d --name spike-rustfs -p 127.0.0.1:19000:9000 -e RUSTFS_ACCESS_KEY=$($s.key) -e RUSTFS_SECRET_KEY=$($s.secret) rustfs/rustfs:latest | Out-Null
    @{ identities = @(@{ name = 'spike'; credentials = @(@{ accessKey = $s.key; secretKey = $s.secret }); actions = @('Admin','Read','List','Tagging','Write') }) } |
        ConvertTo-Json -Depth 5 | Set-Content "$work\s3.json" -Encoding ascii
    docker run -d --name spike-seaweed -p 127.0.0.1:18333:8333 -v "${work}\s3.json:/etc/s3.json:ro" chrislusf/seaweedfs:latest server -dir=/data -s3 -s3.config=/etc/s3.json | Out-Null

    # SFTP: atmoz/sftp, user spike (password) + ed25519/rsa public keys
    Remove-Item "$work\keys\*" -Force -ErrorAction SilentlyContinue
    ssh-keygen -q -t ed25519 -N '""' -f "$work\keys\id_ed25519" | Out-Null
    ssh-keygen -q -t rsa -b 3072 -N '""' -f "$work\keys\id_rsa" | Out-Null
    New-Item -ItemType Directory -Force "$work\pub" | Out-Null
    Copy-Item "$work\keys\*.pub" "$work\pub\"
    docker run -d --name spike-sftp -p 127.0.0.1:12222:22 -v "${work}\pub:/home/spike/.ssh/keys:ro" atmoz/sftp:alpine "spike:$($s.pw):1001:1001:data" | Out-Null
    Start-Sleep 3
    # synthetic tree: 10 x 10 dirs x 30 files, one dir with 2500 files, metadata samples
    $sh = @'
set -e
cd /home/spike/data
mkdir -p tree big meta
for d in 0 1 2 3 4 5 6 7 8 9; do for s in 0 1 2 3 4 5 6 7 8 9; do
  mkdir -p tree/d$d/s$s
  for f in $(seq 0 29); do head -c $(( (d*1000 + s*100 + f*37) % 4096 )) /dev/zero > tree/d$d/s$s/f$f.bin; done
done; done
for i in $(seq 0 2499); do : > big/item$i.dat; done
mv big tree/big
echo hello > meta/file.txt
mkdir meta/dir
ln -s file.txt meta/link-to-file
ln -s dir meta/link-to-dir
ln -s does-not-exist meta/broken-link
U=$(printf '\303\234'); oe=$(printf '\303\266'); ss=$(printf '\303\237'); ae=$(printf '\303\244'); ue=$(printf '\303\274')
mkdir "tree/Ordner mit ${U}mlaut"; echo x > "tree/Ordner mit ${U}mlaut/Gr${oe}${ss}e ${ae}${oe}${ue}.txt"
touch -d '2020-01-02 03:04:05' meta/file.txt
chmod 640 meta/file.txt
chown -R 1001:1001 tree meta
du -sb tree | cut -f1
find tree -type f | wc -l
'@
    # write as UTF-8 without BOM and LF (piping from Windows PowerShell would mangle umlauts)
    [IO.File]::WriteAllText("$work\seed.sh", ($sh -replace "`r", ''), (New-Object Text.UTF8Encoding $false))
    docker cp "$work\seed.sh" spike-sftp:/tmp/seed.sh | Out-Null
    docker exec spike-sftp sh /tmp/seed.sh
    'up'
}

if ($Phase -eq 'bench') {
    $s = Get-Content "$work\secrets.json" | ConvertFrom-Json
    $env:SPIKE_S3_KEY = $s.key; $env:SPIKE_S3_SECRET = $s.secret; $env:SPIKE_SFTP_PW = $s.pw
    if ($DelayMs -gt 0) {
        foreach ($c in $containers) {
            docker run --rm --net "container:$c" --cap-add NET_ADMIN alpine:3.20 sh -c "apk add -q iproute2-tc >/dev/null && tc qdisc replace dev eth0 root netem delay ${DelayMs}ms" | Out-Null
        }
        "netem: +$DelayMs ms on container egress (= +$DelayMs ms RTT)"
    }
    dotnet build $bench -c Release -v q -nologo | Out-Null
    $exe = Join-Path $PSScriptRoot 'Bench\bin\Release\net10.0\Bench.exe'
    $seed = if ($DelayMs -eq 0) { 'seed' } else { 'noseed' }
    foreach ($t in @(@{n='RustFS'; u='http://127.0.0.1:19000'}, @{n='SeaweedFS'; u='http://127.0.0.1:18333'})) {
        "=== S3 $($t.n) ==="
        & $exe s3put $t.u spike-probe-$($DelayMs)
        & $exe s3 $t.u spike-bucket $seed 8
        "--- cold start ($($t.n)) ---"
        & $exe s3cold $t.u spike-bucket
    }
    "=== SFTP ==="
    & $exe sftp 127.0.0.1 12222 spike /data
    & $exe sftp 127.0.0.1 12222 spike /data "$work\keys\id_ed25519"
    & $exe sftp 127.0.0.1 12222 spike /data "$work\keys\id_rsa"
    "--- cold start (SFTP) ---"
    & $exe sftpcold 127.0.0.1 12222 spike
    if ($DelayMs -eq 0) { "=== AWS profiles without AWSSDK.SSO/SSOOIDC/SecurityToken ==="; & $exe awsprofiles }
    if ($DelayMs -gt 0) {
        foreach ($c in $containers) {
            docker run --rm --net "container:$c" --cap-add NET_ADMIN alpine:3.20 sh -c "apk add -q iproute2-tc >/dev/null && tc qdisc del dev eth0 root" | Out-Null
        }
    }
}

if ($Phase -eq 'down') {
    docker rm -f -v $containers 2>$null | Out-Null   # -v: also the anonymous data volumes
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    'down'
}
