namespace OnvifWebDav.App.UI;

/// <summary>手写界面用的小工具，省掉一堆重复的布局代码（不用设计器，方便一次改一堆字段）。</summary>
internal static class FormBuilder
{
    public static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(620, 0),
        ForeColor = Color.FromArgb(110, 116, 128),
        Margin = new Padding(0, 0, 0, 8),
    };

    public static Label Head(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold),
        Margin = new Padding(0, 4, 0, 10),
    };

    public static TextBox Text(string value, bool password = false, int width = 320) => new()
    {
        Text = value,
        Width = width,
        UseSystemPasswordChar = password,
        Margin = new Padding(0, 0, 0, 8),
    };

    public static NumericUpDown Num(decimal value, decimal min, decimal max, int decimals = 0) => new()
    {
        // 顺序不能反：NumericUpDown 在 Value 赋值时就校验范围，先给 Value 会抛 ArgumentOutOfRange
        Minimum = min,
        Maximum = max,
        DecimalPlaces = decimals,
        Value = Math.Clamp(value, min, max),
        Width = 120,
        Margin = new Padding(0, 0, 0, 8),
    };

    public static CheckBox Check(string text, bool value) => new()
    {
        Text = text,
        Checked = value,
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 8),
    };

    public static ComboBox Combo((string Label, object Value)[] items, object? selected)
    {
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 220,
            Margin = new Padding(0, 0, 0, 8),
            DataSource = items.Select(i => new ComboItem(i.Label, i.Value)).ToList(),
            DisplayMember = nameof(ComboItem.Label),
            ValueMember = nameof(ComboItem.Value),
        };

        if (selected is not null) combo.SelectedValue = selected;
        if (combo.SelectedIndex < 0 && combo.Items.Count > 0) combo.SelectedIndex = 0;

        return combo;
    }

    public sealed record ComboItem(string Label, object Value);

    /// <summary>两列表单：左标签右控件。</summary>
    public static TableLayoutPanel Form(params (string Label, Control Control)[] rows)
    {
        var table = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Padding = new Padding(0),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        foreach (var (label, control) in rows)
        {
            var l = new Label
            {
                Text = label,
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 6, 8, 8),
            };
            table.Controls.Add(l);
            table.Controls.Add(control);
        }

        return table;
    }

    /// <summary>带标题的分组框（内容纵向排列）。</summary>
    public static GroupBox Group(string title, params Control[] children)
    {
        var box = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 8, 12, 12),
            Margin = new Padding(0, 0, 0, 10),
        };

        // 注意：这里不能用 Dock=Top + 父级 AutoSize —— 两者互相依赖，在某些主题下会把分组框压成一条线。
        // 用固定起点 + AutoSize，让 GroupBox 按内容算大小。
        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Location = new Point(10, 22),
            Padding = new Padding(0),
            Margin = new Padding(0),
        };

        foreach (var c in children)
        {
            if (c is TableLayoutPanel table)
            {
                table.Dock = DockStyle.None;
                table.AutoSize = true;
            }
            stack.Controls.Add(c);
        }

        box.Controls.Add(stack);
        return box;
    }

    public static Panel Scroll(params Control[] children)
    {
        var host = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(14) };
        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Dock = DockStyle.Top,
        };
        stack.Controls.AddRange(children);
        host.Controls.Add(stack);
        return host;
    }
}
