using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace CfgLogToolWpf;

public class EntryVM : INotifyPropertyChanged
{
    public required string ItemObj { get; init; }
    public required string OriginalDate { get; init; }
    public string ItemName { get; init; } = "";
    public string ItemType { get; init; } = "";
    public string ItemMark { get; init; } = "";
    public string ItemCode { get; init; } = "";

    private string _date = "";
    public string ItemDate
    {
        get => _date;
        set { _date = value; PC(nameof(ItemDate)); PC(nameof(State)); }
    }

    private bool _isDeleted;
    public bool IsDeleted
    {
        get => _isDeleted;
        set { _isDeleted = value; PC(nameof(IsDeleted)); PC(nameof(State)); }
    }

    public bool IsEdited => !IsDeleted && ItemDate != OriginalDate;

    public string State => IsDeleted ? "已删除" : (IsEdited ? "已改时间" : "");

    public event PropertyChangedEventHandler? PropertyChanged;
    private void PC(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<EntryVM> _rows = [];
    private string _originalText = "";
    private string? _loadedFile;

    public MainWindow()
    {
        InitializeComponent();
        Grid.ItemsSource = _rows;
    }

    // ---------- 打开 / 拖拽 ----------

    private void BtnOpen_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "打开 .cfg 日志", Filter = "cfg 日志 (*.cfg)|*.cfg|所有文件 (*.*)|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        LoadFile(dlg.FileName);
    }

    private void Window_DragOver(object sender, DragEventArgs e) => e.Handled = true;

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            LoadFile(files[0]);
    }

    private void LoadFile(string path)
    {
        try
        {
            var text = CfgLogCore.DecodeFile(path);
            var fa = CfgLogCore.ParseFileAttrs(text);
            _originalText = text;
            _loadedFile = path;

            _rows.Clear();
            foreach (var a in CfgLogCore.ParseEntries(text))
                _rows.Add(new EntryVM
                {
                    ItemObj = a.GetValueOrDefault("ItemObj"),
                    OriginalDate = a.GetValueOrDefault("ItemDate"),
                    ItemDate = a.GetValueOrDefault("ItemDate"),
                    ItemName = a.GetValueOrDefault("ItemName"),
                    ItemType = a.GetValueOrDefault("ItemType"),
                    ItemMark = a.GetValueOrDefault("ItemMark"),
                    ItemCode = a.GetValueOrDefault("ItemCode"),
                });

            TbFile.Text = path;
            TbFileInfo.Text = $"日志编号: {fa.GetValueOrDefault("ItemCode", "?")}\n" +
                              $"类型: {fa.GetValueOrDefault("ItemType", "?")}\n" +
                              $"创建: {fa.GetValueOrDefault("Creationtime", "?")}";
            DpNewDate.SelectedDate = DateTime.Now;
            UpdateStatus("已加载");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "打开失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- 筛选 ----------

    private void TbFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        var keyword = TbFilter.Text.Trim();
        var view = CollectionViewSource.GetDefaultView(_rows);
        view.Filter = string.IsNullOrEmpty(keyword)
            ? null
            : o => o is EntryVM vm &&
                   (vm.ItemDate.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || vm.ItemName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || vm.ItemType.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || vm.ItemMark.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || vm.ItemCode.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || vm.ItemObj.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    // ---------- 右侧面板操作 ----------

    private void Grid_SelectedItemChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Grid.SelectedItem is EntryVM vm)
        {
            TbCurDate.Text = $"{vm.ItemDate}  ({vm.ItemName} / {vm.ItemType} / {vm.ItemMark})";
            if (DateTime.TryParseExact(vm.ItemDate, "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var cur))
            {
                DpNewDate.SelectedDate = cur.Date;
                TbNewTime.Text = cur.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            }
        }
        else TbCurDate.Text = "—";
    }

    private void BtnApplyDate_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not EntryVM vm) { Hint("请先在表格中选择一条记录"); return; }
        if (vm.IsDeleted) { Hint("该条目已标记删除，请先恢复"); return; }
        if (DpNewDate.SelectedDate is not DateTime d) { Hint("请选择新日期"); return; }
        var t = TbNewTime.Text.Trim();
        if (!TimeSpan.TryParseExact(t, new[] { @"hh\:mm\:ss", @"h\:mm\:ss", @"hh\:mm", @"h\:mm" },
                CultureInfo.InvariantCulture, out var time) || time >= TimeSpan.FromDays(1))
        { Hint("时间格式无效，应为 HH:mm:ss"); return; }

        var newDate = d.Date.Add(time).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        if (newDate == vm.ItemDate) { Hint("时间未变化"); return; }
        vm.ItemDate = newDate;
        vm.IsDeleted = false;
        UpdateStatus("已标记时间修改");
    }

    private void BtnDeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var sel = Grid.SelectedItems.Cast<EntryVM>().ToList();
        if (sel.Count == 0) { Hint("请先选择要删除的行（可按住 Ctrl/Shift 多选）"); return; }
        foreach (var vm in sel) vm.IsDeleted = true;
        UpdateStatus("已标记删除");
    }

    private void BtnRestoreSelected_Click(object sender, RoutedEventArgs e)
    {
        var sel = Grid.SelectedItems.Cast<EntryVM>().ToList();
        if (sel.Count == 0) { Hint("请先选择要恢复的行"); return; }
        foreach (var vm in sel) { vm.IsDeleted = false; vm.ItemDate = vm.OriginalDate; }
        UpdateStatus("已恢复");
    }

    private void BtnReload_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedFile is null) return;
        if (MessageBox.Show(this, "放弃全部未保存的删除/改时间标记，重新加载原文件？", "确认",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        LoadFile(_loadedFile);
    }

    // ---------- 保存 ----------

    private (string Text, int Del, int Edit)? BuildEditedText()
    {
        var delObjs = _rows.Where(r => r.IsDeleted).Select(r => r.ItemObj).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dateEdits = _rows.Where(r => !r.IsDeleted && r.ItemDate != r.OriginalDate)
                             .ToDictionary(r => r.ItemObj, r => r.ItemDate, StringComparer.OrdinalIgnoreCase);
        if (delObjs.Count == 0 && dateEdits.Count == 0) { Hint("没有任何修改可保存"); return null; }

        var text = _originalText;
        if (delObjs.Count > 0)
        {
            var (t, removed) = CfgLogCore.RemoveByItemObjs(text, delObjs);
            if (removed != delObjs.Count)
                throw new InvalidOperationException($"内部校验失败: 应删 {delObjs.Count} 条, 实际匹配 {removed} 条");
            text = t;
        }
        foreach (var (obj, newDate) in dateEdits)
            text = CfgLogCore.SetDateByObj(text, obj, newDate);
        return (text, delObjs.Count, dateEdits.Count);
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e) => Save(asDialog: false);

    private void BtnSaveAs_Click(object sender, RoutedEventArgs e) => Save(asDialog: true);

    private void Save(bool asDialog)
    {
        if (_loadedFile is null) { Hint("请先打开文件"); return; }
        try
        {
            var built = BuildEditedText();
            if (built is null) return;
            var (newText, del, edit) = built.Value;

            string output;
            if (asDialog)
            {
                var dlg = new SaveFileDialog
                {
                    Title = "保存修改后的 cfg",
                    Filter = "cfg 日志 (*.cfg)|*.cfg",
                    FileName = Path.GetFileNameWithoutExtension(_loadedFile) + "_edit.cfg",
                    InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(_loadedFile)),
                };
                if (dlg.ShowDialog(this) != true) return;
                output = dlg.FileName;
            }
            else
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(_loadedFile))!;
                string baseName = Path.GetFileNameWithoutExtension(_loadedFile);
                output = Path.Combine(dir, baseName + "_edit.cfg");
                int i = 1;
                while (File.Exists(output))
                    output = Path.Combine(dir, $"{baseName}_edit{i++}.cfg");
            }

            File.WriteAllBytes(output, CfgLogCore.EncodeText(newText));

            // 回读验证
            var back = CfgLogCore.DecodeFile(output);
            var okEntries = CfgLogCore.ParseEntries(back).Count;
            if (back != newText)
                throw new InvalidOperationException("回读验证失败: 新文件解压后与预期内容不一致");

            UpdateStatus($"已保存: {output} · 删 {del} / 改 {edit} · 剩余 {okEntries} 条 · 回读验证通过");
            MessageBox.Show(this,
                $"保存成功\n\n{output}\n\n删除 {del} 条, 改时间 {edit} 条, 剩余 {okEntries} 条\n回读验证: 通过",
                "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{output}\"") { UseShellExecute = true }); } catch { /* 非 Windows 忽略 */ }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- 导出三件套 ----------

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedFile is null) { Hint("请先打开文件"); return; }
        try
        {
            var dlg = new OpenFolderDialog { Title = "选择导出目录" };
            if (dlg.ShowDialog(this) != true) return;
            string dir = dlg.FolderName;
            string baseName = Path.GetFileNameWithoutExtension(_loadedFile);

            // 导出为"当前编辑视图"(已删除的不导出, 已改时间的用新时间) — 与表格所见一致
            var kept = _rows.Where(r => !r.IsDeleted)
                .Select(r => new Dictionary<string, string>
                {
                    ["ItemDate"] = r.ItemDate, ["ItemName"] = r.ItemName, ["ItemType"] = r.ItemType,
                    ["ItemMark"] = r.ItemMark, ["ItemCode"] = r.ItemCode, ["ItemObj"] = r.ItemObj,
                }).ToList();
            var fa = CfgLogCore.ParseFileAttrs(_originalText);

            File.WriteAllText(Path.Combine(dir, baseName + ".xml"),
                CfgLogCore.BuildFixedXml(_originalText), new System.Text.UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, baseName + ".csv"),
                CfgLogCore.BuildCsv(kept), new System.Text.UTF8Encoding(true));
            File.WriteAllText(Path.Combine(dir, baseName + "_readable.txt"),
                CfgLogCore.BuildReadable(Path.GetFileName(_loadedFile), fa, kept), new System.Text.UTF8Encoding(false));

            UpdateStatus($"已导出到 {dir}");
            MessageBox.Show(this, $"已导出:\n{baseName}.xml (原始)\n{baseName}.csv (当前视图)\n{baseName}_readable.txt (当前视图)",
                "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- 状态 ----------

    private void UpdateStatus(string prefix)
    {
        int total = _rows.Count;
        int del = _rows.Count(r => r.IsDeleted);
        int edit = _rows.Count(r => r.IsDeleted == false && r.ItemDate != r.OriginalDate);
        TbStatus.Text = $"{prefix} — 共 {total} 条 · 标记删除 {del} · 改时间 {edit} · 保存后剩 {total - del} 条";
    }

    private void Hint(string msg) => UpdateStatus(msg);
}
