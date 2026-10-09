using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace YushuAfterSales
{
    public partial class ErrorDiagnosisWindow : Window
    {
        public string ErrorCode { get; private set; }
        public string TargetPath { get; private set; }

        public ErrorDiagnosisWindow()
        {
            InitializeComponent();
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择需要诊断的程序",
                Filter = "Windows 程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) == true) TargetPathBox.Text = dialog.FileName;
        }

        private void AcceptButton_Click(object sender, RoutedEventArgs e)
        {
            string code = (ErrorCodeBox.Text ?? String.Empty).Trim();
            if (code.Length == 0 && String.IsNullOrWhiteSpace(TargetPathBox.Text))
            {
                MessageBox.Show("请填写错误码或选择目标 EXE。", "错误码诊断", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            ErrorCode = code;
            TargetPath = String.IsNullOrWhiteSpace(TargetPathBox.Text) ? null : Path.GetFullPath(TargetPathBox.Text);
            DialogResult = true;
        }
    }
}
