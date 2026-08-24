namespace Zeus.Samples.WinForms.QuickStart;

/// <summary>
/// 由 WinForms 设计器维护的布局。不要在此写 Zeus 挂接或业务逻辑。
/// </summary>
partial class MainForm
{
    /// <summary>设计器组件容器。</summary>
    private System.ComponentModel.IContainer components = null;

    private TableLayoutPanel layoutRoot;
    private Label labelChannel;
    private Label stateLabel;
    private Label labelTemperature;
    private Label temperatureLabel;
    private Label labelSetpoint;
    private Label setpointLabel;
    private Label labelHeater;
    private Label heaterLabel;
    private Label labelAlarm;
    private Label alarmLabel;
    private Button toggleTemperatureButton;

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && components is not null)
        {
            components.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// 设计器生成的控件初始化。
    /// </summary>
    private void InitializeComponent()
    {
        layoutRoot = new TableLayoutPanel();
        labelChannel = new Label();
        stateLabel = new Label();
        labelTemperature = new Label();
        temperatureLabel = new Label();
        labelSetpoint = new Label();
        setpointLabel = new Label();
        labelHeater = new Label();
        heaterLabel = new Label();
        labelAlarm = new Label();
        alarmLabel = new Label();
        toggleTemperatureButton = new Button();
        layoutRoot.SuspendLayout();
        SuspendLayout();
        //
        // layoutRoot
        //
        layoutRoot.ColumnCount = 2;
        layoutRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80F));
        layoutRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layoutRoot.Controls.Add(labelChannel, 0, 0);
        layoutRoot.Controls.Add(stateLabel, 1, 0);
        layoutRoot.Controls.Add(labelTemperature, 0, 1);
        layoutRoot.Controls.Add(temperatureLabel, 1, 1);
        layoutRoot.Controls.Add(labelSetpoint, 0, 2);
        layoutRoot.Controls.Add(setpointLabel, 1, 2);
        layoutRoot.Controls.Add(labelHeater, 0, 3);
        layoutRoot.Controls.Add(heaterLabel, 1, 3);
        layoutRoot.Controls.Add(labelAlarm, 0, 4);
        layoutRoot.Controls.Add(alarmLabel, 1, 4);
        layoutRoot.Controls.Add(toggleTemperatureButton, 1, 5);
        layoutRoot.Dock = DockStyle.Fill;
        layoutRoot.Location = new Point(0, 0);
        layoutRoot.Name = "layoutRoot";
        layoutRoot.Padding = new Padding(16);
        layoutRoot.RowCount = 6;
        layoutRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
        layoutRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
        layoutRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
        layoutRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
        layoutRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
        layoutRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
        layoutRoot.Size = new Size(520, 380);
        layoutRoot.TabIndex = 0;
        //
        // labelChannel
        //
        labelChannel.Dock = DockStyle.Fill;
        labelChannel.Name = "labelChannel";
        labelChannel.Text = "通道";
        labelChannel.TextAlign = ContentAlignment.MiddleLeft;
        //
        // stateLabel
        //
        stateLabel.Dock = DockStyle.Fill;
        stateLabel.Name = "stateLabel";
        stateLabel.TextAlign = ContentAlignment.MiddleLeft;
        //
        // labelTemperature
        //
        labelTemperature.Dock = DockStyle.Fill;
        labelTemperature.Name = "labelTemperature";
        labelTemperature.Text = "温度";
        labelTemperature.TextAlign = ContentAlignment.MiddleLeft;
        //
        // temperatureLabel
        //
        temperatureLabel.Dock = DockStyle.Fill;
        temperatureLabel.Name = "temperatureLabel";
        temperatureLabel.TextAlign = ContentAlignment.MiddleLeft;
        //
        // labelSetpoint
        //
        labelSetpoint.Dock = DockStyle.Fill;
        labelSetpoint.Name = "labelSetpoint";
        labelSetpoint.Text = "设定值";
        labelSetpoint.TextAlign = ContentAlignment.MiddleLeft;
        //
        // setpointLabel
        //
        setpointLabel.Dock = DockStyle.Fill;
        setpointLabel.Name = "setpointLabel";
        setpointLabel.TextAlign = ContentAlignment.MiddleLeft;
        //
        // labelHeater
        //
        labelHeater.Dock = DockStyle.Fill;
        labelHeater.Name = "labelHeater";
        labelHeater.Text = "加热器";
        labelHeater.TextAlign = ContentAlignment.MiddleLeft;
        //
        // heaterLabel
        //
        heaterLabel.Dock = DockStyle.Fill;
        heaterLabel.Name = "heaterLabel";
        heaterLabel.TextAlign = ContentAlignment.MiddleLeft;
        //
        // labelAlarm
        //
        labelAlarm.Dock = DockStyle.Fill;
        labelAlarm.Name = "labelAlarm";
        labelAlarm.Text = "报警";
        labelAlarm.TextAlign = ContentAlignment.MiddleLeft;
        //
        // alarmLabel
        //
        alarmLabel.Dock = DockStyle.Fill;
        alarmLabel.Name = "alarmLabel";
        alarmLabel.TextAlign = ContentAlignment.MiddleLeft;
        //
        // toggleTemperatureButton
        //
        toggleTemperatureButton.Dock = DockStyle.Fill;
        toggleTemperatureButton.Name = "toggleTemperatureButton";
        toggleTemperatureButton.TabIndex = 1;
        toggleTemperatureButton.Text = "模拟高温";
        toggleTemperatureButton.UseVisualStyleBackColor = true;
        //
        // MainForm
        //
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(520, 380);
        Controls.Add(layoutRoot);
        Font = new Font("Segoe UI", 10F);
        Name = "MainForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Zeus WinForms QuickStart";
        layoutRoot.ResumeLayout(false);
        ResumeLayout(false);
    }
}
