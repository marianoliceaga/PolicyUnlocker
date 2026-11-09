using System.Diagnostics;
using Microsoft.Win32;

namespace PolicyUnlocker;

public sealed class MainForm : Form
{
    private readonly List<PolicyEntry> _entries = new();
    private readonly HashSet<PolicyEntry> _ticked = new();
    private readonly List<Button> _buttons = new();

    private readonly Label _warning = new()
    {
        AutoSize = true,
        ForeColor = Color.DarkRed,
        Text = "Removing policy values can change Windows, browser, antivirus or company-managed behaviour. " +
               "Every change is preceded by a registry backup in " + PolicyScanner.BackupRoot + ".",
    };

    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(0, 4, 0, 0) };
    private readonly Label _mdm = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly TextBox _filter = new() { Width = 380 };

    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        GridLines = true,
        HideSelection = false,
    };

    public MainForm()
    {
        Text = "PolicyUnlocker (Administrator)";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1150, 680);
        MinimumSize = new Size(900, 480);

        _list.Columns.Add("Hive", 60);
        _list.Columns.Add("Key", 520);
        _list.Columns.Add("Value", 220);
        _list.Columns.Add("Type", 95);
        _list.Columns.Add("Data", 320);
        _list.ItemChecked += (_, e) =>
        {
            var entry = (PolicyEntry)e.Item.Tag!;
            if (e.Item.Checked) _ticked.Add(entry);
            else _ticked.Remove(entry);
            UpdateStatus();
        };

        _filter.TextChanged += (_, _) => ApplyFilter();

        var filterRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        filterRow.Controls.Add(new Label { Text = "Filter:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        filterRow.Controls.Add(_filter);

        var buttonRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        buttonRow.Controls.Add(Btn("Refresh", (_, _) => Reload()));
        buttonRow.Controls.Add(Btn("Tick all shown", (_, _) => SetAllShown(true)));
        buttonRow.Controls.Add(Btn("Untick all", (_, _) => { _ticked.Clear(); SetAllShown(false); }));
        buttonRow.Controls.Add(Btn("Remove ticked...", RemoveTicked_Click));
        buttonRow.Controls.Add(Btn("REMOVE ALL + reset GP files...", RemoveAll_Click, 240));
        buttonRow.Controls.Add(Btn("Open gpedit.msc", (_, _) => OpenGpedit()));
        buttonRow.Controls.Add(Btn("Open backups folder", (_, _) => OpenBackups()));

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // 0 warning
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // 1 status
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // 2 MDM info
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // 3 filter
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));      // 4 list
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // 5 buttons
        layout.Controls.Add(_warning, 0, 0);
        layout.Controls.Add(_status, 0, 1);
        layout.Controls.Add(_mdm, 0, 2);
        layout.Controls.Add(filterRow, 0, 3);
        layout.Controls.Add(_list, 0, 4);
        layout.Controls.Add(buttonRow, 0, 5);

        Controls.Add(layout);
        Shown += (_, _) => Reload();
    }

    private Button Btn(string text, EventHandler onClick, int width = 150)
    {
        var b = new Button { Text = text, Width = width, Height = 34, Margin = new Padding(0, 0, 8, 8) };
        b.Click += onClick;
        _buttons.Add(b);
        return b;
    }

    // ------------------------------------------------------------ data / view

    private void Reload()
    {
        _entries.Clear();
        _entries.AddRange(PolicyScanner.Scan());
        _ticked.IntersectWith(_entries);

        var mdm = PolicyScanner.GetMdmEnrollments();
        _mdm.Text = mdm.Count == 0
            ? "MDM enrollment: none detected."
            : "MDM enrollment detected: " + string.Join("; ", mdm) +
              ". Organisation management can re-apply policies; this tool does not remove enrollments.";

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var needle = _filter.Text.Trim();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in _entries)
        {
            if (needle.Length > 0 &&
                !$"{e.FullKey} {e.ValueName} {e.Data}".Contains(needle, StringComparison.OrdinalIgnoreCase))
                continue;

            var item = new ListViewItem(new[]
            {
                e.HiveName,
                e.KeyPath,
                e.ValueName.Length == 0 ? "(Default)" : e.ValueName,
                e.ValueKind,
                e.Data,
            })
            {
                Tag = e,
                Checked = _ticked.Contains(e),
            };
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        UpdateStatus();
    }

    private void SetAllShown(bool tick)
    {
        _list.BeginUpdate();
        foreach (ListViewItem item in _list.Items)
            item.Checked = tick;
        _list.EndUpdate();
    }

    private void UpdateStatus() =>
        _status.Text = $"{_entries.Count} policy value(s) found   |   showing {_list.Items.Count}   |   ticked: {_ticked.Count}";

    // ---------------------------------------------------------------- actions

    private async void RemoveTicked_Click(object? sender, EventArgs e)
    {
        var targets = _entries.Where(_ticked.Contains).ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show(this, "Tick the policy values you want to remove first.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var answer = MessageBox.Show(this,
            $"Remove {targets.Count} ticked policy value(s)?\n\n" +
            "A registry backup is saved first to:\n" + PolicyScanner.BackupRoot + "\n\n" +
            "Windows will then re-read policy (gpupdate /force).",
            "Confirm removal", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes) return;

        await RunAsync(() => PolicyScanner.Apply(targets, deletePolicyFiles: false));
    }

    private async void RemoveAll_Click(object? sender, EventArgs e)
    {
        var targets = _entries.ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show(this, "No policy values were found.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var answer = MessageBox.Show(this,
            $"This removes ALL {targets.Count} listed policy values, including ones set by browsers, " +
            "antivirus, VPN or company software, AND deletes the local Group Policy files " +
            "(System32\\GroupPolicy\\Machine\\Registry.pol and ...\\User\\Registry.pol).\n\n" +
            "A backup is created first in:\n" + PolicyScanner.BackupRoot + "\n\nContinue?",
            "Confirm REMOVE ALL", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        await RunAsync(() => PolicyScanner.Apply(targets, deletePolicyFiles: true));
    }

    private async Task RunAsync(Func<OperationResult> operation)
    {
        SetBusy(true);
        OperationResult? result = null;
        try
        {
            result = await Task.Run(operation);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Operation failed, see details:\n" + ex.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }

        if (result != null)
        {
            _ticked.Clear();
            MessageBox.Show(this, result.Summary(), Text, MessageBoxButtons.OK,
                result.Errors.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        Reload();
    }

    private void SetBusy(bool busy)
    {
        UseWaitCursor = busy;
        foreach (var b in _buttons)
            b.Enabled = !busy;
    }

    private void OpenGpedit()
    {
        try
        {
            Process.Start(new ProcessStartInfo("gpedit.msc") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Could not open gpedit.msc:\n" + ex.Message +
                "\n\ngpedit.msc is only included in Windows Pro, Enterprise and Education.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void OpenBackups()
    {
        Directory.CreateDirectory(PolicyScanner.BackupRoot);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{PolicyScanner.BackupRoot}\"") { UseShellExecute = true });
    }
}
