using DiskTree.Core;

namespace DiskTree.Ui;

/// <summary>
/// What the charts of the side panel (file types, age) describe: a folder with everything below it, or just the files
/// of one folder (<paramref name="OwnOnly"/>), and the name to show in the title.
/// </summary>
public readonly record struct ChartTarget(Node? Node, bool OwnOnly, string Title);
