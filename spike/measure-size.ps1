param([string]$Repo, [string]$Out)
$ErrorActionPreference = 'Stop'
$proj = Join-Path $Repo 'DiskTree.csproj'
foreach ($v in 'none','s3','sftp','both','s3sso') {
    foreach ($k in 'full','slim') {
        Remove-Item (Join-Path $Repo 'obj'),(Join-Path $Repo 'bin') -Recurse -Force -ErrorAction SilentlyContinue
        $dir = Join-Path $Out "$v-$k"
        $common = @('-c','Release',"-p:SpikeProbe=$v",'-p:Version=0.0.0-spike','-o',$dir,'-v','q','-nologo')
        if ($k -eq 'full') { dotnet publish $proj @common -p:SelfContained=true | Out-Null }
        else { dotnet publish $proj @common -p:SelfContained=false -p:EnableCompressionInSingleFile=false | Out-Null }
        if ($LASTEXITCODE -ne 0) { throw "publish $v $k failed" }
        "{0}`t{1}`t{2}" -f $v, $k, (Get-Item (Join-Path $dir 'DiskTree.exe')).Length
    }
}
