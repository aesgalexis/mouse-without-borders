# Shared by build/test entry points. Never follow links during recursive cleanup.
function Remove-MwbGeneratedDirectory([string]$Path, [string]$Root) {
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $target = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if (-not $target.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Cleanup target is outside its generated root: $target"
    }
    $ancestor = $target
    while ($ancestor.Length -ge $rootPath.Length) {
        if (Test-Path -LiteralPath $ancestor) {
            if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing cleanup through a link: $ancestor"
            }
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    if (Test-Path -LiteralPath $target) {
        if (Test-Path -LiteralPath $target -PathType Container) {
            $links = @(Get-ChildItem -LiteralPath $target -Force -Recurse -Attributes ReparsePoint)
            if ($links.Count) { throw "Refusing cleanup containing links: $target" }
        }
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}
