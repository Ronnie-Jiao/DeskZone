[CmdletBinding()]
param(
    [ValidateSet('Install', 'Uninstall', 'Verify')]
    [string]$Action = 'Install',
    [string]$SourcePath = '',
    [string]$InstallPath = '',
    [switch]$CreateDesktopShortcut,
    [switch]$Interactive
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$dataPath = Join-Path $env:LOCALAPPDATA 'DeskZone'
$startupRegistryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startupRegistryValueName = 'DeskZone'
$installLocationPath = Join-Path $dataPath 'install-location.json'
if ([string]::IsNullOrWhiteSpace($InstallPath)) {
    $savedInstallPath = ''
    if (Test-Path -LiteralPath $installLocationPath -PathType Leaf) {
        try {
            $savedInstallPath = (Get-Content -LiteralPath $installLocationPath -Raw | ConvertFrom-Json).InstallDirectory
        }
        catch {
            $savedInstallPath = ''
        }
    }

    $InstallPath = if ([string]::IsNullOrWhiteSpace($savedInstallPath)) {
        Join-Path $env:LOCALAPPDATA 'Programs\DeskZone'
    }
    else {
        $savedInstallPath
    }
}
$InstallPath = [IO.Path]::GetFullPath($InstallPath)

function Assert-SafeInstallPath {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $dataFullPath = [IO.Path]::GetFullPath($dataPath).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $leaf = Split-Path -Leaf $fullPath

    if ([string]::IsNullOrWhiteSpace($leaf) -or $leaf -ne 'DeskZone') {
        throw "安装目录必须是名为 DeskZone 的专用目录，当前值：$fullPath"
    }
    if ($fullPath -eq $dataFullPath -or $fullPath.StartsWith($dataFullPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "安装目录不能位于用户数据目录中：$dataFullPath"
    }
    if ($fullPath -eq [IO.Path]::GetPathRoot($fullPath).TrimEnd([IO.Path]::DirectorySeparatorChar)) {
        throw "不允许把磁盘根目录作为安装目录：$fullPath"
    }

    return $fullPath
}

function Stop-DeskZone {
    $processes = @(Get-Process -Name 'DeskZone' -ErrorAction SilentlyContinue)
    foreach ($process in $processes) {
        if ($process.MainWindowHandle -ne 0) {
            [void]$process.CloseMainWindow()
        }
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ([DateTime]::UtcNow -lt $deadline) {
        $remaining = @(Get-Process -Name 'DeskZone' -ErrorAction SilentlyContinue)
        if ($remaining.Count -eq 0) {
            return
        }
        Start-Sleep -Milliseconds 250
    }

    Get-Process -Name 'DeskZone' -ErrorAction SilentlyContinue | Stop-Process -Force
}

function Backup-UserData {
    if (-not (Test-Path -LiteralPath $dataPath)) {
        return $null
    }

    $backupRoot = Join-Path $dataPath 'backups'
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backupPath = Join-Path $backupRoot "installer-before-update-$stamp"
    New-Item -ItemType Directory -Path $backupPath -Force | Out-Null

    foreach ($name in @('deskzone.db', 'deskzone.db-wal', 'deskzone.db-shm', 'settings.json')) {
        $source = Join-Path $dataPath $name
        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination $backupPath -Force
        }
    }

    $manifest = @{
        CreatedAtUtc = [DateTime]::UtcNow.ToString('O')
        Reason = 'Before installer update'
        DataDirectory = $dataPath
        InstallDirectory = $InstallPath
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backupPath 'backup-manifest.json') -Encoding UTF8
    return $backupPath
}

function Set-Shortcut {
    param(
        [Parameter(Mandatory)][string]$ShortcutPath,
        [Parameter(Mandatory)][string]$TargetPath,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [string]$Arguments = ''
    )

    $parent = Split-Path -Parent $ShortcutPath
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $TargetPath
    $shortcut.Arguments = $Arguments
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.IconLocation = "$TargetPath,0"
    $shortcut.Description = '桌序 DeskZone'
    $shortcut.Save()
}

function Get-DeskZoneDesktopShortcuts {
    $desktopDirectory = [Environment]::GetFolderPath('DesktopDirectory')
    if ([string]::IsNullOrWhiteSpace($desktopDirectory) -or -not (Test-Path -LiteralPath $desktopDirectory -PathType Container)) {
        return @()
    }

    $shell = New-Object -ComObject WScript.Shell
    $shortcuts = @()
    foreach ($shortcutFile in @(Get-ChildItem -LiteralPath $desktopDirectory -Filter '*.lnk' -File -ErrorAction SilentlyContinue)) {
        try {
            $shortcut = $shell.CreateShortcut($shortcutFile.FullName)
            $targetName = if ([string]::IsNullOrWhiteSpace($shortcut.TargetPath)) {
                ''
            }
            else {
                [IO.Path]::GetFileName($shortcut.TargetPath)
            }
            $isPreview = $shortcut.Arguments -match '(^|\s)--preview(\s|$)'
            if ($targetName -ieq 'DeskZone.exe' -and -not $isPreview) {
                $shortcuts += [PSCustomObject]@{
                    Path = $shortcutFile.FullName
                    Name = $shortcutFile.Name
                    TargetPath = $shortcut.TargetPath
                    Arguments = $shortcut.Arguments
                    CreationTimeUtc = $shortcutFile.CreationTimeUtc
                }
            }
        }
        catch {
            # 损坏或暂时无法读取的其它快捷方式不应阻止更新安装。
        }
    }

    return $shortcuts
}

function Sync-DeskZoneDesktopShortcut {
    param(
        [Parameter(Mandatory)][string]$ExecutablePath
    )

    $desktopDirectory = [Environment]::GetFolderPath('DesktopDirectory')
    if ([string]::IsNullOrWhiteSpace($desktopDirectory)) {
        throw '找不到当前用户的桌面目录，无法更新 DeskZone 快捷方式。'
    }

    $existingShortcuts = @(Get-DeskZoneDesktopShortcuts)
    $canonicalCandidates = @(
        $existingShortcuts |
            Sort-Object CreationTimeUtc, Name |
            Select-Object -First 1
    )

    $created = $false
    if ($canonicalCandidates.Count -gt 0) {
        $canonicalShortcut = $canonicalCandidates[0]
        $canonicalPath = $canonicalShortcut.Path
    }
    else {
        $canonicalPath = Join-Path $desktopDirectory 'DeskZone.lnk'
        $created = $true
    }

    Set-Shortcut `
        -ShortcutPath $canonicalPath `
        -TargetPath $ExecutablePath `
        -WorkingDirectory (Split-Path -Parent $ExecutablePath)

    $duplicateShortcuts = @(
        $existingShortcuts | Where-Object { $_.Path -ne $canonicalPath }
    )
    foreach ($duplicate in $duplicateShortcuts) {
        Remove-ShortcutIfPresent $duplicate.Path
    }

    return [PSCustomObject]@{
        Path = $canonicalPath
        Created = $created
        Updated = -not $created
        RemovedDuplicates = $duplicateShortcuts.Count
    }
}

function Remove-ShortcutIfPresent {
    param([Parameter(Mandatory)][string]$Path)
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Force
    }
}

function Set-DeskZoneStartupRegistration {
    param(
        [Parameter(Mandatory)][string]$ExecutablePath
    )

    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        throw "无法设置开机自动启动：找不到 DeskZone.exe：$ExecutablePath"
    }

    New-Item -Path $startupRegistryPath -Force | Out-Null
    Set-ItemProperty -Path $startupRegistryPath `
        -Name $startupRegistryValueName `
        -Value ('"{0}"' -f $ExecutablePath) `
        -Type String
}

function Remove-DeskZoneStartupRegistration {
    if (Test-Path -LiteralPath $startupRegistryPath) {
        Remove-ItemProperty -Path $startupRegistryPath `
            -Name $startupRegistryValueName `
            -ErrorAction SilentlyContinue
    }
}

function Install-DeskZone {
    param(
        [Parameter(Mandatory)][string]$PackagePath,
        [scriptblock]$ProgressReporter
    )

    function Report-Progress {
        param(
            [int]$Percent,
            [string]$Message
        )

        if ($ProgressReporter) {
            & $ProgressReporter $Percent $Message
        }
        else {
            Write-Host $Message
        }
    }

    if (-not (Test-Path -LiteralPath $PackagePath -PathType Container)) {
        throw "发布目录不存在：$PackagePath。请先运行 Publish-DeskZone.ps1。"
    }
    $sourceExe = Join-Path $PackagePath 'DeskZone.exe'
    if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
        throw "发布目录中没有 DeskZone.exe：$PackagePath"
    }

    $backupPath = $null
    $stagePath = Join-Path (Split-Path -Parent $InstallPath) ".DeskZone-staging-$PID-$(Get-Date -Format yyyyMMddHHmmss)"
    $previousPath = Join-Path (Split-Path -Parent $InstallPath) ".DeskZone-previous-$PID-$(Get-Date -Format yyyyMMddHHmmss)"
    New-Item -ItemType Directory -Path $stagePath -Force | Out-Null

    try {
        Report-Progress 10 '正在准备安装…'
        Stop-DeskZone
        Report-Progress 25 '正在备份现有数据…'
        $backupPath = Backup-UserData

        Report-Progress 45 '正在复制程序文件…'
        Get-ChildItem -LiteralPath $PackagePath -Force | Copy-Item -Destination $stagePath -Recurse -Force

        $hadExistingInstall = Test-Path -LiteralPath $InstallPath
        if ($hadExistingInstall) {
            Report-Progress 65 '正在替换旧版本…'
            Move-Item -LiteralPath $InstallPath -Destination $previousPath
        }

        try {
            Move-Item -LiteralPath $stagePath -Destination $InstallPath
        }
        catch {
            if ($hadExistingInstall -and (Test-Path -LiteralPath $previousPath)) {
                Move-Item -LiteralPath $previousPath -Destination $InstallPath
            }
            throw
        }

        $metadata = @{
            InstalledAtUtc = [DateTime]::UtcNow.ToString('O')
            InstallDirectory = $InstallPath
            DataDirectory = $dataPath
            DataPolicy = 'User data is never removed by update or normal uninstall.'
        }
        $metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $InstallPath '.deskzone-install.json') -Encoding UTF8
        New-Item -ItemType Directory -Path $dataPath -Force | Out-Null
        @{
            InstallDirectory = $InstallPath
            UpdatedAtUtc = [DateTime]::UtcNow.ToString('O')
        } | ConvertTo-Json | Set-Content -LiteralPath $installLocationPath -Encoding UTF8

        if (Test-Path -LiteralPath $previousPath) {
            Remove-Item -LiteralPath $previousPath -Recurse -Force
        }

        Report-Progress 75 '正在创建开始菜单快捷方式…'
        $startMenuShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'DeskZone.lnk'
        Set-Shortcut -ShortcutPath $startMenuShortcut -TargetPath (Join-Path $InstallPath 'DeskZone.exe') -WorkingDirectory $InstallPath

        if (-not $hadExistingInstall) {
            Report-Progress 82 '正在设置开机自动启动…'
            Set-DeskZoneStartupRegistration -ExecutablePath (Join-Path $InstallPath 'DeskZone.exe')
        }

        Report-Progress 85 '正在更新桌面快捷方式…'
        $desktopShortcutResult = Sync-DeskZoneDesktopShortcut -ExecutablePath (Join-Path $InstallPath 'DeskZone.exe')

        Report-Progress 100 '安装完成'

        Write-Host "DeskZone 安装/升级完成。"
        Write-Host "程序目录：$InstallPath"
        Write-Host "数据目录：$dataPath（未删除）"
        if ($backupPath) {
            Write-Host "升级前备份：$backupPath"
        }

        return [PSCustomObject]@{
            DesktopShortcut = $desktopShortcutResult
        }
    }
    finally {
        if (Test-Path -LiteralPath $stagePath) {
            Remove-Item -LiteralPath $stagePath -Recurse -Force
        }
    }
}

function ConvertTo-DeskZoneInstallPath {
    param([Parameter(Mandatory)][string]$SelectedPath)

    $fullPath = [IO.Path]::GetFullPath($SelectedPath)
    if ((Split-Path -Leaf $fullPath) -ne 'DeskZone') {
        $fullPath = Join-Path $fullPath 'DeskZone'
    }

    return $fullPath
}

function Initialize-InstallerVisualTypes {
    if ('DeskZoneInstaller.GlassPanel' -as [type]) {
        return
    }

    Add-Type -ReferencedAssemblies System.Windows.Forms, System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DeskZoneInstaller
{
    internal static class Drawing
    {
        internal static GraphicsPath RoundedPath(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), radius * 2);
            if (diameter <= 1)
            {
                path.AddRectangle(bounds);
                return path;
            }

            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    public sealed class GlassPanel : Panel
    {
        public int CornerRadius { get; set; }
        public Color SurfaceColor { get; set; }
        public Color StrokeColor { get; set; }
        public int StrokeWidth { get; set; }

        public GlassPanel()
        {
            CornerRadius = 16;
            SurfaceColor = Color.FromArgb(220, 255, 255, 255);
            StrokeColor = Color.FromArgb(150, 142, 192, 246);
            StrokeWidth = 1;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            if (bounds.Width < 1 || bounds.Height < 1)
            {
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Drawing.RoundedPath(bounds, CornerRadius))
            using (var fill = new SolidBrush(SurfaceColor))
            using (var stroke = new Pen(StrokeColor, StrokeWidth))
            {
                e.Graphics.FillPath(fill, path);
                if (StrokeWidth > 0)
                {
                    e.Graphics.DrawPath(stroke, path);
                }
            }
        }
    }

    public sealed class GradientButton : Control, IButtonControl
    {
        private bool _hovered;
        private bool _defaultButton;

        public int CornerRadius { get; set; }
        public Color StartColor { get; set; }
        public Color EndColor { get; set; }
        public Color StrokeColor { get; set; }
        public float StrokeWidth { get; set; }

        public GradientButton()
        {
            CornerRadius = 12;
            StartColor = Color.FromArgb(255, 0, 96, 252);
            EndColor = Color.FromArgb(255, 2, 208, 252);
            StrokeColor = Color.FromArgb(160, 116, 181, 255);
            StrokeWidth = 1;
            // 这是纯自绘按钮，不叠加 WinForms 原生 Button 的背景或图片层。
            BackColor = Color.White;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
        }

        public DialogResult DialogResult { get; set; }

        public void NotifyDefault(bool value)
        {
            _defaultButton = value;
            Invalidate();
        }

        public void PerformClick()
        {
            if (Enabled)
            {
                OnClick(EventArgs.Empty);
            }
        }

        protected override bool ShowFocusCues
        {
            get { return false; }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 先清空自己的绘制面；按钮的圆角、渐变和描边全部由 OnPaint 绘制。
            e.Graphics.Clear(BackColor);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Width < 2 || Height < 2)
            {
                return;
            }

            using (var path = Drawing.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius))
            {
                var previous = Region;
                Region = new Region(path);
                if (previous != null)
                {
                    previous.Dispose();
                }
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // 描边向内留出 1 像素，避免圆角区域把边缘描边裁掉。
            var bounds = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            if (bounds.Width < 1 || bounds.Height < 1)
            {
                return;
            }

            var start = Enabled ? StartColor : Color.FromArgb(150, StartColor);
            var end = Enabled ? EndColor : Color.FromArgb(150, EndColor);
            if (_hovered && Enabled)
            {
                start = ControlPaint.Light(start, 0.10f);
                end = ControlPaint.Light(end, 0.10f);
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Drawing.RoundedPath(bounds, CornerRadius))
            using (var brush = new LinearGradientBrush(bounds, start, end, LinearGradientMode.Horizontal))
            using (var stroke = new Pen(StrokeColor, StrokeWidth))
            {
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(stroke, path);
            }

            var color = Enabled ? ForeColor : Color.FromArgb(150, ForeColor);
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                bounds,
                color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    public sealed class AccentProgressBar : Control
    {
        private int _value;

        public int Value
        {
            get { return _value; }
            set
            {
                _value = Math.Max(0, Math.Min(100, value));
                Invalidate();
            }
        }

        public Color TrackColor { get; set; }
        public Color StartColor { get; set; }
        public Color EndColor { get; set; }

        public AccentProgressBar()
        {
            TrackColor = Color.FromArgb(135, 140, 177, 216);
            StartColor = Color.FromArgb(255, 0, 96, 252);
            EndColor = Color.FromArgb(255, 2, 208, 252);
            Height = 12;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            if (bounds.Width < 1 || bounds.Height < 1)
            {
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var trackPath = Drawing.RoundedPath(bounds, Math.Max(1, Height / 2)))
            using (var track = new SolidBrush(TrackColor))
            {
                e.Graphics.FillPath(track, trackPath);
            }

            var fillWidth = (int)Math.Round(bounds.Width * (_value / 100.0));
            if (fillWidth <= 0)
            {
                return;
            }

            var fillBounds = new Rectangle(0, 0, Math.Max(1, fillWidth), bounds.Height);
            using (var fillPath = Drawing.RoundedPath(fillBounds, Math.Max(1, Height / 2)))
            using (var fill = new LinearGradientBrush(fillBounds, StartColor, EndColor, LinearGradientMode.Horizontal))
            {
                e.Graphics.FillPath(fill, fillPath);
            }
        }
    }

    public static class WindowChrome
    {
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        public static void Drag(Form form)
        {
            ReleaseCapture();
            SendMessage(form.Handle, 0xA1, 0x2, 0);
        }

        public static void ApplyRoundedRegion(Form form, int radius)
        {
            if (form.WindowState == FormWindowState.Maximized)
            {
                form.Region = null;
                return;
            }

            using (var path = Drawing.RoundedPath(new Rectangle(0, 0, form.Width, form.Height), radius))
            {
                var previous = form.Region;
                form.Region = new Region(path);
                if (previous != null)
                {
                    previous.Dispose();
                }
            }
        }
    }
}
'@
}

function New-InstallerLabel {
    param(
        [Parameter(Mandatory)][string]$Text,
        [float]$FontSize = 10,
        [bool]$Bold = $false,
        [System.Drawing.Color]$Color = [System.Drawing.Color]::FromArgb(11, 36, 71)
    )

    $label = New-Object System.Windows.Forms.Label
    $label.Text = $Text
    $label.AutoSize = $true
    $label.BackColor = [System.Drawing.Color]::Transparent
    $label.ForeColor = $Color
    $label.Font = New-Object System.Drawing.Font(
        'Microsoft YaHei UI',
        $FontSize,
        $(if ($Bold) { [System.Drawing.FontStyle]::Bold } else { [System.Drawing.FontStyle]::Regular }))
    return $label
}

function New-InstallerWindowButton {
    param([Parameter(Mandatory)][string]$Text)

    $button = New-Object System.Windows.Forms.Button
    $button.Text = $Text
    $button.Width = 44
    $button.Dock = 'Right'
    $button.FlatStyle = 'Flat'
    $button.FlatAppearance.BorderSize = 0
    $button.FlatAppearance.MouseOverBackColor = [System.Drawing.Color]::FromArgb(35, 11, 36, 71)
    $button.FlatAppearance.MouseDownBackColor = [System.Drawing.Color]::FromArgb(65, 11, 36, 71)
    $button.BackColor = [System.Drawing.Color]::Transparent
    $button.ForeColor = [System.Drawing.Color]::FromArgb(16, 28, 76)
    $button.Font = New-Object System.Drawing.Font('Segoe UI Symbol', 14, [System.Drawing.FontStyle]::Regular)
    $button.TabStop = $false
    return $button
}

function Resolve-InstallerResourcePath {
    param([Parameter(Mandatory)][string]$FileName)

    $candidates = @(
        (Join-Path $PSScriptRoot (Join-Path 'assets' $FileName)),
        (Join-Path $PSScriptRoot $FileName)
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    throw "安装器缺少资源：$FileName"
}

function Show-DeskZoneInstallWizard {
    param([Parameter(Mandatory)][string]$PackagePath)

    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    Initialize-InstallerVisualTypes

    $backgroundPath = Resolve-InstallerResourcePath 'InstallerBackground.png'
    $logoPath = Resolve-InstallerResourcePath 'DeskZoneLogoCompact.png'
    $folderIconPath = Resolve-InstallerResourcePath 'InstallerFolderIcon.png'
    $iconPath = Resolve-InstallerResourcePath 'DeskZoneLogo.ico'

    $backgroundImage = [System.Drawing.Image]::FromFile($backgroundPath)
    $logoImage = [System.Drawing.Image]::FromFile($logoPath)
    $folderIconImage = [System.Drawing.Image]::FromFile($folderIconPath)
    $installerIcon = [System.Drawing.Icon]::new($iconPath)

    $form = New-Object System.Windows.Forms.Form
    $form.Text = '安装 DeskZone'
    $form.StartPosition = 'CenterScreen'
    $form.FormBorderStyle = 'None'
    $form.ClientSize = New-Object System.Drawing.Size(1040, 630)
    $form.MinimumSize = New-Object System.Drawing.Size(900, 570)
    $form.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 10)
    $form.BackColor = [System.Drawing.Color]::White
    $form.BackgroundImage = $backgroundImage
    $form.BackgroundImageLayout = 'Stretch'
    $form.KeyPreview = $true
    $form.Icon = $installerIcon
    $form.ShowIcon = $true

    $header = New-Object DeskZoneInstaller.GlassPanel
    $header.Dock = 'Top'
    $header.Height = 46
    $header.CornerRadius = 0
    $header.SurfaceColor = [System.Drawing.Color]::FromArgb(220, 252, 254, 255)
    $header.StrokeColor = [System.Drawing.Color]::Transparent
    $header.StrokeWidth = 0

    $closeButton = New-InstallerWindowButton '×'
    $maximizeButton = New-InstallerWindowButton '□'
    $minimizeButton = New-InstallerWindowButton '−'
    $header.Controls.Add($closeButton)
    $header.Controls.Add($maximizeButton)
    $header.Controls.Add($minimizeButton)

    $headerIcon = New-Object System.Windows.Forms.PictureBox
    $headerIcon.Image = $logoImage
    $headerIcon.SizeMode = 'Zoom'
    $headerIcon.BackColor = [System.Drawing.Color]::Transparent
    $headerIcon.SetBounds(28, 9, 28, 28)
    $header.Controls.Add($headerIcon)

    $headerTitle = New-InstallerLabel -Text '安装  DeskZone' -FontSize 12 -Bold $true
    $headerTitle.Location = New-Object System.Drawing.Point(68, 13)
    $header.Controls.Add($headerTitle)
    $form.Controls.Add($header)

    $dragWindow = {
        param($sender, $eventArgs)
        if ($eventArgs.Button -eq [System.Windows.Forms.MouseButtons]::Left) {
            [DeskZoneInstaller.WindowChrome]::Drag($form)
        }
    }
    $header.Add_MouseDown($dragWindow)
    $headerTitle.Add_MouseDown($dragWindow)
    $headerIcon.Add_MouseDown($dragWindow)
    $header.Add_MouseDoubleClick({
        if ($form.WindowState -eq [System.Windows.Forms.FormWindowState]::Maximized) {
            $form.WindowState = [System.Windows.Forms.FormWindowState]::Normal
        }
        else {
            $form.WindowState = [System.Windows.Forms.FormWindowState]::Maximized
        }
    })
    $minimizeButton.Add_Click({ $form.WindowState = [System.Windows.Forms.FormWindowState]::Minimized })
    $maximizeButton.Add_Click({
        if ($form.WindowState -eq [System.Windows.Forms.FormWindowState]::Maximized) {
            $form.WindowState = [System.Windows.Forms.FormWindowState]::Normal
        }
        else {
            $form.WindowState = [System.Windows.Forms.FormWindowState]::Maximized
        }
    })
    $closeButton.Add_Click({ $form.Close() })

    $pathLabel = New-InstallerLabel -Text '安装目录' -FontSize 12 -Bold $true
    $form.Controls.Add($pathLabel)

    $pathContainer = New-Object DeskZoneInstaller.GlassPanel
    $pathContainer.CornerRadius = 10
    $pathContainer.SurfaceColor = [System.Drawing.Color]::FromArgb(228, 255, 255, 255)
    $pathContainer.StrokeColor = [System.Drawing.Color]::FromArgb(185, 126, 182, 246)
    $form.Controls.Add($pathContainer)

    $folderIcon = New-Object System.Windows.Forms.PictureBox
    $folderIcon.Image = $folderIconImage
    $folderIcon.SizeMode = 'Zoom'
    $folderIcon.BackColor = [System.Drawing.Color]::Transparent
    $folderIcon.SetBounds(16, 9, 32, 29)
    $pathContainer.Controls.Add($folderIcon)

    $pathBox = New-Object System.Windows.Forms.TextBox
    $pathBox.Text = $InstallPath
    $pathBox.BorderStyle = 'None'
    $pathBox.BackColor = [System.Drawing.Color]::FromArgb(252, 254, 255)
    $pathBox.ForeColor = [System.Drawing.Color]::FromArgb(11, 36, 71)
    $pathBox.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 11)
    $pathContainer.Controls.Add($pathBox)

    $browseButton = New-Object DeskZoneInstaller.GradientButton
    $browseButton.Text = '浏览'
    $browseButton.CornerRadius = 10
    $browseButton.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 11, [System.Drawing.FontStyle]::Bold)
    $browseButton.ForeColor = [System.Drawing.Color]::FromArgb(11, 36, 71)
    $browseButton.StartColor = [System.Drawing.Color]::FromArgb(242, 255, 255, 255)
    $browseButton.EndColor = [System.Drawing.Color]::FromArgb(232, 230, 243, 255)
    $browseButton.StrokeColor = [System.Drawing.Color]::FromArgb(185, 126, 182, 246)
    $browseButton.StrokeWidth = 1
    $form.Controls.Add($browseButton)
    $browseButton.Add_Click({
        $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
        $dialog.Description = '选择 DeskZone 程序安装位置'
        $selectedPath = Split-Path -Parent $pathBox.Text
        if (Test-Path -LiteralPath $selectedPath) {
            $dialog.SelectedPath = $selectedPath
        }

        if ($dialog.ShowDialog($form) -eq [System.Windows.Forms.DialogResult]::OK) {
            $pathBox.Text = ConvertTo-DeskZoneInstallPath $dialog.SelectedPath
        }
    })

    $note = New-InstallerLabel -Text '安装完成后会创建或更新桌面 DeskZone 快捷方式。' -FontSize 10 -Color ([System.Drawing.Color]::FromArgb(77, 108, 155))
    $form.Controls.Add($note)

    $progress = New-Object DeskZoneInstaller.AccentProgressBar
    $progress.Value = 0
    $form.Controls.Add($progress)

    $status = New-InstallerLabel -Text '准备安装' -FontSize 12 -Bold $true -Color ([System.Drawing.Color]::FromArgb(71, 105, 154))
    $form.Controls.Add($status)

    $installButton = New-Object DeskZoneInstaller.GradientButton
    $installButton.Text = '安装'
    $installButton.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 12, [System.Drawing.FontStyle]::Bold)
    $installButton.ForeColor = [System.Drawing.Color]::White
    $installButton.StartColor = [System.Drawing.Color]::FromArgb(0, 96, 252)
    $installButton.EndColor = [System.Drawing.Color]::FromArgb(2, 208, 252)
    $installButton.StrokeColor = [System.Drawing.Color]::FromArgb(65, 104, 174, 255)
    $form.Controls.Add($installButton)

    $cancelButton = New-Object DeskZoneInstaller.GradientButton
    $cancelButton.Text = '取消'
    $cancelButton.CornerRadius = 10
    $cancelButton.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 12, [System.Drawing.FontStyle]::Bold)
    $cancelButton.ForeColor = [System.Drawing.Color]::FromArgb(11, 36, 71)
    $cancelButton.StartColor = [System.Drawing.Color]::FromArgb(240, 255, 255, 255)
    $cancelButton.EndColor = [System.Drawing.Color]::FromArgb(224, 234, 244, 255)
    $cancelButton.StrokeColor = [System.Drawing.Color]::FromArgb(185, 126, 182, 246)
    $cancelButton.StrokeWidth = 1
    $cancelButton.Add_Click({ $form.Close() })
    $form.Controls.Add($cancelButton)

    $openButton = New-Object DeskZoneInstaller.GradientButton
    $openButton.Text = '立即打开'
    $openButton.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 12, [System.Drawing.FontStyle]::Bold)
    $openButton.ForeColor = [System.Drawing.Color]::White
    $openButton.StartColor = [System.Drawing.Color]::FromArgb(0, 96, 252)
    $openButton.EndColor = [System.Drawing.Color]::FromArgb(2, 208, 252)
    $openButton.StrokeColor = [System.Drawing.Color]::FromArgb(65, 104, 174, 255)
    $openButton.Visible = $false
    $openButton.Add_Click({
        Start-Process -FilePath (Join-Path $openButton.Tag 'DeskZone.exe')
        $form.Close()
    })
    $form.Controls.Add($openButton)

    $finishButton = New-Object DeskZoneInstaller.GradientButton
    $finishButton.Text = '完成'
    $finishButton.CornerRadius = 10
    $finishButton.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 12, [System.Drawing.FontStyle]::Bold)
    $finishButton.ForeColor = [System.Drawing.Color]::FromArgb(11, 36, 71)
    $finishButton.StartColor = [System.Drawing.Color]::FromArgb(240, 255, 255, 255)
    $finishButton.EndColor = [System.Drawing.Color]::FromArgb(224, 234, 244, 255)
    $finishButton.StrokeColor = [System.Drawing.Color]::FromArgb(185, 126, 182, 246)
    $finishButton.StrokeWidth = 1
    $finishButton.Visible = $false
    $finishButton.Add_Click({ $form.Close() })
    $form.Controls.Add($finishButton)

    $layoutControls = {
        if ($form.IsDisposed -or $form.WindowState -eq [System.Windows.Forms.FormWindowState]::Minimized) {
            return
        }

        $width = $form.ClientSize.Width
        $height = $form.ClientSize.Height
        $margin = [Math]::Max(54, [Math]::Round($width * 0.066))
        $browseWidth = 128
        $buttonHeight = 48
        $buttonGap = 18
        $footerBottom = 46
        $actionTop = $height - $footerBottom - $buttonHeight
        $statusTop = $actionTop - 49
        $progressTop = $statusTop - 31
        $noteTop = $progressTop - 47
        $inputTop = $noteTop - 62
        $labelTop = $inputTop - 33
        $pathWidth = [Math]::Max(300, $width - ($margin * 2) - $browseWidth - $buttonGap)

        $pathLabel.Location = New-Object System.Drawing.Point($margin, $labelTop)
        $pathContainer.SetBounds($margin, $inputTop, $pathWidth, 48)
        $folderIcon.SetBounds(16, 9, 32, 29)
        $pathBox.SetBounds(56, 13, [Math]::Max(80, $pathWidth - 70), 24)
        $browseButton.SetBounds($margin + $pathWidth + $buttonGap, $inputTop, $browseWidth, 48)
        $note.Location = New-Object System.Drawing.Point($margin, $noteTop)
        $progress.SetBounds($margin, $progressTop, $width - ($margin * 2), 12)
        $status.Location = New-Object System.Drawing.Point($margin, $statusTop)

        $secondaryWidth = 138
        $primaryWidth = 160
        $secondaryLeft = $width - $margin - $secondaryWidth
        $primaryLeft = $secondaryLeft - $buttonGap - $primaryWidth
        $installButton.SetBounds($primaryLeft, $actionTop, $primaryWidth, $buttonHeight)
        $cancelButton.SetBounds($secondaryLeft, $actionTop, $secondaryWidth, $buttonHeight)
        $openButton.SetBounds($primaryLeft, $actionTop, $primaryWidth, $buttonHeight)
        $finishButton.SetBounds($secondaryLeft, $actionTop, $secondaryWidth, $buttonHeight)
    }

    $form.Add_Load({
        [DeskZoneInstaller.WindowChrome]::ApplyRoundedRegion($form, 18)
        & $layoutControls
    })
    $form.Add_SizeChanged({
        [DeskZoneInstaller.WindowChrome]::ApplyRoundedRegion($form, 18)
        & $layoutControls
    })

    $installButton.Add_Click({
        try {
            $targetPath = ConvertTo-DeskZoneInstallPath $pathBox.Text
            Assert-SafeInstallPath $targetPath | Out-Null

            $installButton.Enabled = $false
            $cancelButton.Enabled = $false
            $browseButton.Enabled = $false
            $closeButton.Enabled = $false
            $pathBox.ReadOnly = $true
            $progress.Value = 0
            $reportProgress = {
                param([int]$Percent, [string]$Message)
                $progress.Value = [Math]::Max(0, [Math]::Min(100, $Percent))
                $status.Text = $Message
                $form.Refresh()
                [System.Windows.Forms.Application]::DoEvents()
            }

            $originalInstallPath = $script:InstallPath
            try {
                $script:InstallPath = $targetPath
                $installResult = Install-DeskZone -PackagePath $PackagePath -ProgressReporter $reportProgress
            }
            finally {
                $script:InstallPath = $originalInstallPath
            }

            $status.Text = '安装完成。可立即打开 DeskZone。'
            $headerTitle.Text = '安装完成  DeskZone'
            if ($installResult.DesktopShortcut.Created) {
                $note.Text = '已创建桌面和开始菜单快捷方式。'
            }
            elseif ($installResult.DesktopShortcut.RemovedDuplicates -gt 0) {
                $note.Text = '已更新原有快捷方式，并清理重复的桌面入口。'
            }
            else {
                $note.Text = '已更新原有桌面和开始菜单快捷方式。'
            }
            $openButton.Tag = $targetPath
            $installButton.Visible = $false
            $cancelButton.Visible = $false
            $openButton.Visible = $true
            $finishButton.Visible = $true
            $closeButton.Enabled = $true
            $form.AcceptButton = $openButton
            $form.CancelButton = $finishButton
        }
        catch {
            $status.Text = '安装失败，请检查安装目录后重试。'
            $installButton.Enabled = $true
            $cancelButton.Enabled = $true
            $browseButton.Enabled = $true
            $closeButton.Enabled = $true
            $pathBox.ReadOnly = $false
            [System.Windows.Forms.MessageBox]::Show(
                $form,
                $_.Exception.Message,
                'DeskZone 安装失败',
                [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
        }
    })

    $form.AcceptButton = $installButton
    $form.CancelButton = $cancelButton
    try {
        [void]$form.ShowDialog()
    }
    finally {
        $backgroundImage.Dispose()
        $logoImage.Dispose()
        $folderIconImage.Dispose()
        $installerIcon.Dispose()
        $form.Dispose()
    }
}

function Uninstall-DeskZone {
    Stop-DeskZone

    Remove-DeskZoneStartupRegistration

    $startMenuShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'DeskZone.lnk'
    Remove-ShortcutIfPresent $startMenuShortcut
    foreach ($desktopShortcut in @(Get-DeskZoneDesktopShortcuts)) {
        Remove-ShortcutIfPresent $desktopShortcut.Path
    }

    if (Test-Path -LiteralPath $InstallPath) {
        Remove-Item -LiteralPath $InstallPath -Recurse -Force
    }

    Write-Host "DeskZone 程序已卸载。"
    Write-Host "用户数据已保留：$dataPath"
}

function Verify-DeskZone {
    Assert-SafeInstallPath $InstallPath | Out-Null
    $exePath = Join-Path $InstallPath 'DeskZone.exe'
    Write-Host "程序目录：$InstallPath"
    Write-Host "程序存在：$(Test-Path -LiteralPath $exePath)"
    Write-Host "数据目录：$dataPath"
    Write-Host "数据目录存在：$(Test-Path -LiteralPath $dataPath)"
    foreach ($name in @('deskzone.db', 'settings.json', 'backups')) {
        Write-Host "  $name：$(Test-Path -LiteralPath (Join-Path $dataPath $name))"
    }
}

Assert-SafeInstallPath $InstallPath | Out-Null

switch ($Action) {
    'Install' {
        if ([string]::IsNullOrWhiteSpace($SourcePath)) {
            $bundledExe = Join-Path $PSScriptRoot 'DeskZone.exe'
            $SourcePath = if (Test-Path -LiteralPath $bundledExe -PathType Leaf) {
                $PSScriptRoot
            }
            else {
                Join-Path $PSScriptRoot 'output\DeskZone'
            }
        }

        $SourcePath = [IO.Path]::GetFullPath($SourcePath)
        if ($Interactive) {
            Show-DeskZoneInstallWizard $SourcePath
        }
        else {
            Install-DeskZone $SourcePath
        }
    }
    'Uninstall' { Uninstall-DeskZone }
    'Verify' { Verify-DeskZone }
}
