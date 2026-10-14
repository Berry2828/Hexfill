using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace HexFill
{
    public class OffsetDialog : Form
    {
        private Label lblDirection;
        private RadioButton rdoExpand;
        private RadioButton rdoShrink;
        private Label lblDistance;
        private TextBox txtDistance;
        private Label lblLayer;
        private ComboBox cmbLayer;
        private Button btnNewLayer;
        private TextBox txtNewLayer;
        private Button btnOK;
        private Button btnCancel;

        public double OffsetDistance { get; private set; }
        public bool IsExpand { get; set; }  // true = 外扩, false = 内缩
        public string SelectedLayerName { get; private set; }
        public string NewLayerName { get; private set; }
        public string CurrentLayerName { get; set; } = "0";
        public List<string> LayerNames { get; } = new List<string>();

        public OffsetDialog()
        {
            InitializeComponent();
            this.KeyPreview = true;
            this.KeyDown += OffsetDialog_KeyDown;
        }

        private void OffsetDialog_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                this.DialogResult = DialogResult.Cancel;
                this.Close();
            }
        }

        private void InitializeComponent()
        {
            this.Text = "HOF 偏移命令";
            this.Size = new System.Drawing.Size(420, 380);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;

            // 偏移方向
            lblDirection = new Label { Text = "偏移方向:", Left = 20, Top = 20, Width = 70 };
            this.Controls.Add(lblDirection);

            rdoExpand = new RadioButton { Text = "外扩", Left = 100, Top = 20, Width = 80, Checked = true };
            this.Controls.Add(rdoExpand);

            rdoShrink = new RadioButton { Text = "内缩", Left = 190, Top = 20, Width = 80 };
            this.Controls.Add(rdoShrink);

            // 偏移距离
            lblDistance = new Label { Text = "距离:", Left = 20, Top = 60, Width = 70 };
            this.Controls.Add(lblDistance);

            txtDistance = new TextBox { Left = 100, Top = 58, Width = 150, Text = "0.5" };
            this.Controls.Add(txtDistance);

            // 分隔线
            var separator = new Panel { Left = 20, Top = 95, Width = 370, Height = 2, BackColor = System.Drawing.Color.Gray };
            this.Controls.Add(separator);

            // 目标图层
            lblLayer = new Label { Text = "目标图层:", Left = 20, Top = 110, Width = 70 };
            this.Controls.Add(lblLayer);

            cmbLayer = new ComboBox { Left = 100, Top = 108, Width = 200, DropDownStyle = ComboBoxStyle.DropDown };
            this.Controls.Add(cmbLayer);

            // 新建图层（文本框+按钮）
            btnNewLayer = new Button { Text = "新建图层", Left = 305, Top = 136, Width = 80, Height = 25 };
            btnNewLayer.Click += BtnNewLayer_Click;
            this.Controls.Add(btnNewLayer);

            txtNewLayer = new TextBox { Left = 100, Top = 136, Width = 200 };
            this.Controls.Add(txtNewLayer);

            // 按钮
            btnOK = new Button { Text = "确认", Left = 210, Top = 310, Width = 80, Height = 30 };
            btnOK.Click += BtnOK_Click;
            this.Controls.Add(btnOK);

            btnCancel = new Button { Text = "取消", Left = 300, Top = 310, Width = 80, Height = 30 };
            btnCancel.Click += (s, e) => { this.DialogResult = DialogResult.Cancel; this.Close(); };
            this.Controls.Add(btnCancel);

            // 加载图层列表
            LoadLayerNames();
        }

        private void LoadLayerNames()
        {
            RefreshLayers();
        }

        public void RefreshLayers()
        {
            cmbLayer.Items.Clear();
            cmbLayer.Items.Add("0");
            cmbLayer.Items.Add("<当前图层>");
            foreach (string name in LayerNames)
            {
                if (!cmbLayer.Items.Contains(name))
                    cmbLayer.Items.Add(name);
            }
            if (cmbLayer.Items.Count > 1)
                cmbLayer.SelectedIndex = 1;
        }

        private void BtnNewLayer_Click(object sender, EventArgs e)
        {
            string newName = txtNewLayer.Text.Trim();
            if (string.IsNullOrEmpty(newName))
            {
                MessageBox.Show("请输入新图层名称", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 检查是否已存在，如果存在则直接使用
            if (cmbLayer.Items.Contains(newName))
            {
                // 直接选中已存在的图层
                cmbLayer.SelectedItem = newName;
            }
            else
            {
                // 添加新图层并选中
                cmbLayer.Items.Add(newName);
                cmbLayer.SelectedItem = newName;
            }

            txtNewLayer.Clear();

            // 验证输入
            if (!rdoExpand.Checked && !rdoShrink.Checked)
            {
                MessageBox.Show("请选择偏移方向（外扩或内缩）", "输入错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrEmpty(txtDistance.Text))
            {
                MessageBox.Show("请输入偏移距离", "输入错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!TryParseOffsetDistance(txtDistance.Text, out double dist))
            {
                MessageBox.Show("偏移距离必须为正数", "输入错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 设置返回值
            this.OffsetDistance = dist;
            this.IsExpand = rdoExpand.Checked;
            this.SelectedLayerName = newName;
            this.NewLayerName = newName;

            // 关闭对话框，进入下一步框选对象
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private void BtnOK_Click(object sender, EventArgs e)
        {
            // 验证偏移方向
            if (!rdoExpand.Checked && !rdoShrink.Checked)
            {
                MessageBox.Show("请选择偏移方向（外扩或内缩）", "输入错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            IsExpand = rdoExpand.Checked;

            // 验证偏移距离
            if (!TryParseOffsetDistance(txtDistance.Text, out double dist))
            {
                MessageBox.Show("请输入有效的偏移距离（大于0）", "输入错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            OffsetDistance = dist;

            // 获取图层名称
            SelectedLayerName = cmbLayer.Text.Trim();
            if (string.IsNullOrEmpty(SelectedLayerName) || SelectedLayerName == "<当前图层>")
            {
                SelectedLayerName = CurrentLayerName;
            }

            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private static bool TryParseOffsetDistance(string text, out double distance)
        {
            return double.TryParse(text, out distance) && distance > 0
                && !double.IsNaN(distance) && !double.IsInfinity(distance);
        }
    }}
