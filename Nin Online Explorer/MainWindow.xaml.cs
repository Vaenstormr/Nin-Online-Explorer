using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Nin_Online_Explorer
{
    public class FileToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            string name = value?.ToString() ?? "";
            bool isFile = name.EndsWith(".nin", StringComparison.OrdinalIgnoreCase);
            return isFile ? "📄" : "📁";
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotImplementedException();
    }

    public partial class MainWindow : Window
    {
        private static readonly string Salt = "yC9*I^~0%d J4k0k4JhfDkwzpi^|of0~*W5I-r0u7T1IY4S^C6O3^RmV-H-B";
        private static readonly byte[] FixedBytes = { 66, 135, 99, 114 };
        private const int MaxParallelism = 4;

        private List<TreeViewItem> _selectedItems = new List<TreeViewItem>();
        private TreeViewItem _lastSelectedItem = null;

        public MainWindow() => InitializeComponent();

        private Point _startPoint;

        private void TreeFolderStructure_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && _lastSelectedItem?.Tag != null)
            {
                string sourcePath = _lastSelectedItem.Tag.ToString();
                if (sourcePath.EndsWith(".nin"))
                {
                    try
                    {
                        string tempFolder = Path.Combine(Path.GetTempPath(), "NinExplorer");
                        Directory.CreateDirectory(tempFolder);
                        string tempFileName = Path.GetFileNameWithoutExtension(sourcePath) + ".png";
                        string tempFilePath = Path.Combine(tempFolder, tempFileName);

                        var data = DecryptNinFileAsync(sourcePath).GetAwaiter().GetResult();
                        File.WriteAllBytes(tempFilePath, data);

                        DataObject dataObject = new DataObject(DataFormats.FileDrop, new string[] { tempFilePath });

                        DragDrop.DoDragDrop(_lastSelectedItem, dataObject, DragDropEffects.Copy);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("Error en Drag: " + ex.Message);
                    }
                }
            }
        }

        private async void TreeFolderStructure_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);

                var hitTest = VisualTreeHelper.HitTest(TreeFolderStructure, e.GetPosition(TreeFolderStructure));
                var targetItem = GetNearestContainer(hitTest.VisualHit);

                if (targetItem?.Tag != null && files.Length > 0)
                {
                    string targetNinPath = targetItem.Tag.ToString();
                    string sourcePngPath = files[0];

                    if (sourcePngPath.ToLower().EndsWith(".png") && targetNinPath.ToLower().EndsWith(".nin"))
                    {
                        await EncryptPngToNinAsync(sourcePngPath, targetNinPath);
                        await LoadPreviewAsync(targetNinPath);
                        MessageBox.Show("File succesfully replaced");
                    }
                }
            }
        }

        private TreeViewItem GetNearestContainer(DependencyObject element)
        {
            while (element != null && !(element is TreeViewItem))
                element = VisualTreeHelper.GetParent(element);
            return element as TreeViewItem;
        }

        #region CRYPTO ENGINE
        private async System.Threading.Tasks.Task<byte[]> DecryptNinFileAsync(string filePath)
        {
            byte[] input = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);

            return await System.Threading.Tasks.Task.Run(() =>
            {
                using (PasswordDeriveBytes pdb = new PasswordDeriveBytes(Salt, FixedBytes))
                {
                    using (Aes aes = Aes.Create())
                    {
                        aes.KeySize = 256;
                        aes.Key = pdb.GetBytes(32);
                        aes.IV = pdb.GetBytes(16);

                        using (MemoryStream ms = new MemoryStream())
                        {
                            using (CryptoStream cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
                            {
                                cs.Write(input, 0, input.Length);
                                cs.FlushFinalBlock();
                            }
                            return ms.ToArray();
                        }
                    }
                }
            });
        }

        private async System.Threading.Tasks.Task EncryptPngToNinAsync(string source, string dest)
        {
            byte[] input = await File.ReadAllBytesAsync(source).ConfigureAwait(false);

            byte[] encrypted = await System.Threading.Tasks.Task.Run(() =>
            {
                using (PasswordDeriveBytes pdb = new PasswordDeriveBytes(Salt, FixedBytes))
                {
                    using (Aes aes = Aes.Create())
                    {
                        aes.KeySize = 256;
                        aes.Key = pdb.GetBytes(32);
                        aes.IV = pdb.GetBytes(16);

                        using (MemoryStream ms = new MemoryStream())
                        {
                            using (CryptoStream cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                            {
                                cs.Write(input, 0, input.Length);
                                cs.FlushFinalBlock();
                            }
                            return ms.ToArray();
                        }
                    }
                }
            });

            await File.WriteAllBytesAsync(dest, encrypted).ConfigureAwait(false);
        }

        public BitmapImage ConvertBytesToImage(byte[] imageBytes)
        {
            if (imageBytes == null || imageBytes.Length == 0) return null;
            try
            {
                using var ms = new MemoryStream(imageBytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch { return null; }
        }
        #endregion

        #region TREE NAVIGATION & SELECTION (Logic maintained)
        private void BtnSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog();
            if (dialog.ShowDialog() == true)
            {
                TreeFolderStructure.Items.Clear();
                _selectedItems.Clear();
                _lastSelectedItem = null;
                var dirInfo = new DirectoryInfo(dialog.FolderName);

                foreach (var dir in dirInfo.GetDirectories().OrderBy(d => d.Name))
                    TreeFolderStructure.Items.Add(CreateTreeItem(dir));

                foreach (var file in dirInfo.GetFiles("*.nin").OrderBy(f => f.Name))
                    TreeFolderStructure.Items.Add(new TreeViewItem { Header = file.Name, Tag = file.FullName });
            }
        }

        private TreeViewItem CreateTreeItem(DirectoryInfo directoryInfo)
        {
            var node = new TreeViewItem { Header = directoryInfo.Name, IsExpanded = false, Tag = null };
            try
            {
                foreach (var dir in directoryInfo.GetDirectories().OrderBy(d => d.Name))
                    node.Items.Add(CreateTreeItem(dir));
                foreach (var file in directoryInfo.GetFiles("*.nin").OrderBy(f => f.Name))
                    node.Items.Add(new TreeViewItem { Header = file.Name, Tag = file.FullName });
            }
            catch (UnauthorizedAccessException) { }
            return node;
        }

        private async void TreeViewItem_OnSelected(object sender, RoutedEventArgs e)
        {
            var currentItem = e.OriginalSource as TreeViewItem;
            if (currentItem == null) return;

            if ((Keyboard.Modifiers & ModifierKeys.Shift) > 0 && _lastSelectedItem != null)
                PerformShiftSelection(_lastSelectedItem, currentItem);
            else if ((Keyboard.Modifiers & ModifierKeys.Control) > 0)
                ToggleSelection(currentItem);
            else
            {
                ClearAllSelections();
                ToggleSelection(currentItem);
            }

            _lastSelectedItem = currentItem;

            if (currentItem.Tag != null)
            {
                await LoadPreviewAsync(currentItem.Tag.ToString());
            }
            else
            {
                ImagePreview.Source = null;
            }

            e.Handled = true;
        }

        private void ToggleSelection(TreeViewItem item)
        {
            if (!_selectedItems.Contains(item))
            {
                _selectedItems.Add(item);
                item.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(58, 58, 58));
                item.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 162, 232));
            }
        }

        private void ClearAllSelections()
        {
            foreach (var item in _selectedItems)
            {
                item.Background = System.Windows.Media.Brushes.Transparent;
                item.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
                item.IsSelected = false;
            }
            _selectedItems.Clear();
        }

        private void PerformShiftSelection(TreeViewItem start, TreeViewItem end)
        {
            var allItems = GetVisibleItems(TreeFolderStructure);
            int startIndex = allItems.IndexOf(start);
            int endIndex = allItems.IndexOf(end);
            if (startIndex == -1 || endIndex == -1) return;

            for (int i = Math.Min(startIndex, endIndex); i <= Math.Max(startIndex, endIndex); i++)
                ToggleSelection(allItems[i]);
        }

        private List<TreeViewItem> GetVisibleItems(ItemsControl container)
        {
            var list = new List<TreeViewItem>();
            foreach (var item in container.Items)
            {
                var tvi = container.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
                if (tvi != null)
                {
                    list.Add(tvi);
                    if (tvi.IsExpanded) list.AddRange(GetVisibleItems(tvi));
                }
            }
            return list;
        }
        #endregion

        private async System.Threading.Tasks.Task LoadPreviewAsync(string filePath)
        {
            try
            {
                var data = await DecryptNinFileAsync(filePath);
                var bmp = ConvertBytesToImage(data);
                await Dispatcher.InvokeAsync(() => ImagePreview.Source = bmp);
            }
            catch { await Dispatcher.InvokeAsync(() => ImagePreview.Source = null); }
        }

        #region EXPORT & REPLACE OPERATIONS

        private async void BtnExportPng_Click(object sender, RoutedEventArgs e)
        {
            var filesToExport = _selectedItems.Where(i => i.Tag != null).Select(i => i.Tag.ToString()).ToList();
            if (!filesToExport.Any()) return;

            if (filesToExport.Count == 1)
            {
                var saveDialog = new Microsoft.Win32.SaveFileDialog { Filter = "PNG Image|*.png", FileName = Path.GetFileNameWithoutExtension(filesToExport[0]) + ".png" };
                if (saveDialog.ShowDialog() == true)
                {
                    try
                    {
                        var data = await DecryptNinFileAsync(filesToExport[0]);
                        await File.WriteAllBytesAsync(saveDialog.FileName, data);
                    }
                    catch (Exception ex) { MessageBox.Show(ex.Message); }
                }
            }
            else
            {
                var folderDialog = new Microsoft.Win32.OpenFolderDialog();
                if (folderDialog.ShowDialog() == true)
                {
                    int count = 0;
                    var sem = new System.Threading.SemaphoreSlim(MaxParallelism);
                    var tasks = filesToExport.Select(async path =>
                    {
                        await sem.WaitAsync();
                        try
                        {
                            var data = await DecryptNinFileAsync(path);
                            string outPath = Path.Combine(folderDialog.FolderName, Path.GetFileNameWithoutExtension(path) + ".png");
                            await File.WriteAllBytesAsync(outPath, data);
                            System.Threading.Interlocked.Increment(ref count);
                        }
                        catch { }
                        finally { sem.Release(); }
                    });
                    await System.Threading.Tasks.Task.WhenAll(tasks);
                    MessageBox.Show($"Exported {count} files.");
                }
            }
        }

        private void BtnReplaceWithPng_Click(object sender, RoutedEventArgs e)
        {
            if (_lastSelectedItem?.Tag == null) return;
            string targetNinPath = _lastSelectedItem.Tag.ToString();

            var openDialog = new Microsoft.Win32.OpenFileDialog { Filter = "PNG Image|*.png" };
            if (openDialog.ShowDialog() == true)
            {
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        await EncryptPngToNinAsync(openDialog.FileName, targetNinPath);
                        var data = await DecryptNinFileAsync(targetNinPath);
                        await Dispatcher.InvokeAsync(() =>
                        {
                            ImagePreview.Source = ConvertBytesToImage(data);
                            MessageBox.Show("Replaced successfully.");
                        });
                    }
                    catch (Exception ex) { await Dispatcher.InvokeAsync(() => MessageBox.Show(ex.Message)); }
                });
            }
        }

        private async void BtnSmartBatchReplace_Click(object sender, RoutedEventArgs e)
        {
            var selectedNinFiles = _selectedItems.Where(i => i.Tag != null && i.Tag.ToString().EndsWith(".nin")).ToList();
            if (!selectedNinFiles.Any()) return;

            var folderDialog = new Microsoft.Win32.OpenFolderDialog();
            if (folderDialog.ShowDialog() == true)
            {
                var sourcePngs = Directory.GetFiles(folderDialog.FolderName, "*.png")
                    .ToDictionary(p => Path.GetFileNameWithoutExtension(p).ToLower(), p => p);

                int replacedCount = 0;
                var sem = new System.Threading.SemaphoreSlim(MaxParallelism);
                var tasks = selectedNinFiles.Select(async ninItem =>
                {
                    string fullPath = ninItem.Tag.ToString();
                    string name = Path.GetFileNameWithoutExtension(fullPath).ToLower();
                    if (sourcePngs.TryGetValue(name, out var pngPath))
                    {
                        await sem.WaitAsync();
                        try { await EncryptPngToNinAsync(pngPath, fullPath); System.Threading.Interlocked.Increment(ref replacedCount); }
                        finally { sem.Release(); }
                    }
                });
                await System.Threading.Tasks.Task.WhenAll(tasks);
                MessageBox.Show($"Replaced {replacedCount} files.");
            }
        }

        private async void BtnImportFromFile_Click(object sender, RoutedEventArgs e)
        {
            var selectedNinFiles = _selectedItems.Where(i => i.Tag != null && i.Tag.ToString().EndsWith(".nin")).ToList();
            if (!selectedNinFiles.Any()) return;

            var openDialog = new Microsoft.Win32.OpenFileDialog { Filter = "PNG Image|*.png" };
            if (openDialog.ShowDialog() == true)
            {
                int count = 0;
                var sem = new System.Threading.SemaphoreSlim(MaxParallelism);
                var tasks = selectedNinFiles.Select(async item =>
                {
                    await sem.WaitAsync();
                    try { await EncryptPngToNinAsync(openDialog.FileName, item.Tag.ToString()); System.Threading.Interlocked.Increment(ref count); }
                    finally { sem.Release(); }
                });
                await System.Threading.Tasks.Task.WhenAll(tasks);
                MessageBox.Show($"Imported to {count} files.");
            }
        }
        #endregion
        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = e.Uri.AbsoluteUri,
                    UseShellExecute = true
                });
                e.Handled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open the link: " + ex.Message);
            }
        }
    }
}