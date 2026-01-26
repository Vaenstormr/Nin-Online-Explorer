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
    /// <summary>
    /// Implements natural sorting for files (e.g., "file2" comes before "file10").
    /// </summary>
    public class NaturalFileInfoComparer : IComparer<FileInfo>
    {
        [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string psz1, string psz2);

        public int Compare(FileInfo x, FileInfo y) => StrCmpLogicalW(x.Name, y.Name);
    }

    /// <summary>
    /// Implements natural sorting for directories.
    /// </summary>
    public class NaturalDirectoryInfoComparer : IComparer<DirectoryInfo>
    {
        [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string psz1, string psz2);

        public int Compare(DirectoryInfo x, DirectoryInfo y) => StrCmpLogicalW(x.Name, y.Name);
    }

    /// <summary>
    /// Converts a file path string into a representative icon (emoji).
    /// </summary>
    public class FileToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            string name = value?.ToString() ?? "";
            bool isFile = name.EndsWith(".nin", StringComparison.OrdinalIgnoreCase);
            return isFile ? "📄" : "📁"; // Returns page for files, folder for directories
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotImplementedException();
    }

    public partial class MainWindow : Window
    {
        // Security constants for AES encryption/decryption
        private static readonly string Salt = "yC9*I^~0%d J4k0k4JhfDkwzpi^|of0~*W5I-r0u7T1IY4S^C6O3^RmV-H-B";
        private static readonly byte[] FixedBytes = { 66, 135, 99, 114 };
        private const int MaxParallelism = 4; // Limits simultaneous crypto tasks

        // State variables for Drag and Drop and Multi-selection
        private Point _startPoint;
        private TreeViewItem _dragSourceItem;
        private bool _canStartDrag = false;
        private List<TreeViewItem> _selectedItems = new List<TreeViewItem>();
        private TreeViewItem _lastSelectedItem = null;

        public MainWindow() => InitializeComponent();

        #region FUNCIÓN: MODIFICACIÓN DE TRANSPARENCIA

        /// <summary>
        /// Applies transparency to selected files.
        /// </summary>
        private async void BtnApplyOpacity_Click(object sender, RoutedEventArgs e)
        {
            var selectedNinFiles = _selectedItems.Where(i => i.Tag != null && i.Tag.ToString().EndsWith(".nin")).ToList();
            if(!selectedNinFiles.Any())
            {
                MessageBox.Show("Please, select a .nin file first.");
                return;
            }

            double opacityFactor = SliderOpacity.Value / 100.0;
            int count = 0;

            var sem = new System.Threading.SemaphoreSlim(MaxParallelism);
            var tasks = selectedNinFiles.Select(async item =>
            {
                await sem.WaitAsync();
                try
                {
                    await AdjustOpacityAsync(item.Tag.ToString(), opacityFactor);
                    System.Threading.Interlocked.Increment(ref count);
                }
                finally { sem.Release(); }
            });

            await System.Threading.Tasks.Task.WhenAll(tasks);

            // Refresh preview
            if(_lastSelectedItem?.Tag != null)
                await LoadPreviewAsync(_lastSelectedItem.Tag.ToString());

            MessageBox.Show($"Transparency has been adjusted in {count} files.");
        }

        private async System.Threading.Tasks.Task AdjustOpacityAsync(string filePath, double opacityFactor)
        {
            try
            {
                // 1. Decrypt the file to obtain the bytes of the actual image
                byte[] rawData = await DecryptNinFileAsync(filePath);

                // 2. Process image
                byte[] processedData = await System.Threading.Tasks.Task.Run(() =>
                {
                    using var ms = new MemoryStream(rawData);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = ms;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();

                    var convertedBitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                    var wbm = new WriteableBitmap(convertedBitmap);

                    int width = wbm.PixelWidth;
                    int height = wbm.PixelHeight;
                    int[] pixels = new int[width * height];

                    wbm.CopyPixels(pixels, width * 4, 0);

                    for(int i = 0;i < pixels.Length;i++)
                    {
                        uint color = (uint)pixels[i];
                        uint a = (color >> 24) & 0xFF;
                        uint r = (color >> 16) & 0xFF;
                        uint g = (color >> 8) & 0xFF;
                        uint b = color & 0xFF;

                        uint newA;
                        if(opacityFactor >= 1.0)
                        {

                            newA = (a > 0) ? (uint)255 : 0;
                        }
                        else
                        {
                            newA = (uint)(a * opacityFactor);
                        }

                        pixels[i] = (int)((newA << 24) | (r << 16) | (g << 8) | b);
                    }

                    wbm.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);

                    using var outStream = new MemoryStream();
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(wbm));
                    encoder.Save(outStream);
                    return outStream.ToArray();
                });

                // 3. Encrypts and saves the changes.
                await EncryptBytesToNinAsync(processedData, filePath);
            }
            catch(Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error adjusting opacity: " + ex.Message);
            }
        }
        #endregion

        #region DRAG AND DROP LOGIC
        /// <summary>
        /// Detects the start of a drag operation.
        /// </summary>
        private void TreeFolderStructure_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _startPoint = e.GetPosition(TreeFolderStructure);
            _canStartDrag = false;

            var hitTest = VisualTreeHelper.HitTest(TreeFolderStructure, _startPoint);
            if(hitTest != null)
            {
                _dragSourceItem = GetNearestContainer(hitTest.VisualHit);
                // Allow dragging only if the item is a .nin file
                if(_dragSourceItem?.Tag != null && _dragSourceItem.Tag.ToString().EndsWith(".nin", StringComparison.OrdinalIgnoreCase))
                {
                    _canStartDrag = true;
                }
            }
        }

        /// <summary>
        /// Executes the DragDrop effect if the mouse moves beyond the minimum threshold.
        /// </summary>
        private void TreeFolderStructure_MouseMove(object sender, MouseEventArgs e)
        {
            if(e.LeftButton == MouseButtonState.Pressed && _canStartDrag && _dragSourceItem != null)
            {
                Point mousePos = e.GetPosition(null);
                Vector diff = _startPoint - mousePos;

                if(Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    ExecuteDragDrop(_dragSourceItem);
                    _dragSourceItem = null;
                    _canStartDrag = false;
                }
            }
        }

        /// <summary>
        /// Prepares the file for external drag-and-drop by decrypting it to a temp PNG.
        /// </summary>
        private void ExecuteDragDrop(TreeViewItem sourceItem)
        {
            try
            {
                if(sourceItem?.Tag == null)
                    return;
                string sourcePath = sourceItem.Tag.ToString();
                if(!sourcePath.EndsWith(".nin", StringComparison.OrdinalIgnoreCase))
                    return;

                string fileName = Path.GetFileName(sourcePath);
                string nameOnly = fileName.Length > 4 ? fileName.Substring(0, fileName.Length - 4) : fileName;

                // Create a unique temporary directory
                string tempFolder = Path.Combine(Path.GetTempPath(), "NinExplorer", Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempFolder);
                string tempFilePath = Path.Combine(tempFolder, nameOnly + ".png");

                var data = DecryptNinFileSync(sourcePath);
                if(data != null)
                {
                    File.WriteAllBytes(tempFilePath, data);
                    DataObject dataObject = new DataObject(DataFormats.FileDrop, new string[] { tempFilePath });
                    DragDrop.DoDragDrop(sourceItem, dataObject, DragDropEffects.Copy);
                }
            }
            catch(Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error on Drag: " + ex.Message);
            }
        }

        /// <summary>
        /// Handles dropping a PNG file onto a .nin entry to replace it.
        /// </summary>
        private async void TreeFolderStructure_Drop(object sender, DragEventArgs e)
        {
            if(e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                var hitTest = VisualTreeHelper.HitTest(TreeFolderStructure, e.GetPosition(TreeFolderStructure));
                var targetItem = GetNearestContainer(hitTest.VisualHit);

                if(targetItem?.Tag != null && files.Length > 0)
                {
                    string targetNinPath = targetItem.Tag.ToString();
                    string sourcePngPath = files[0];

                    if(sourcePngPath.ToLower().EndsWith(".png") && targetNinPath.ToLower().EndsWith(".nin"))
                    {
                        await EncryptPngToNinAsync(sourcePngPath, targetNinPath);
                        await LoadPreviewAsync(targetNinPath);
                        MessageBox.Show("File successfully replaced");
                    }
                }
            }
        }
        #endregion

        #region CRYPTO ENGINE
        /// <summary>
        /// Decrypts a .nin file synchronously using AES-256 & more functions
        /// </summary>
        private async System.Threading.Tasks.Task EncryptBytesToNinAsync(byte[] input, string dest)
        {
            byte[] encrypted = await System.Threading.Tasks.Task.Run(() =>
            {
                using(PasswordDeriveBytes pdb = new PasswordDeriveBytes(Salt, FixedBytes))
                using(Aes aes = Aes.Create())
                {
                    aes.KeySize = 256;
                    aes.Key = pdb.GetBytes(32);
                    aes.IV = pdb.GetBytes(16);
                    using(MemoryStream ms = new MemoryStream())
                    {
                        using(CryptoStream cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                        {
                            cs.Write(input, 0, input.Length);
                            cs.FlushFinalBlock();
                        }
                        return ms.ToArray();
                    }
                }
            });
            await File.WriteAllBytesAsync(dest, encrypted).ConfigureAwait(false);
        }

        private byte[] DecryptNinFileSync(string filePath)
        {
            byte[] input = File.ReadAllBytes(filePath);
            using(PasswordDeriveBytes pdb = new PasswordDeriveBytes(Salt, FixedBytes))
            using(Aes aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Key = pdb.GetBytes(32);
                aes.IV = pdb.GetBytes(16);
                using(MemoryStream ms = new MemoryStream())
                {
                    using(CryptoStream cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
                    {
                        cs.Write(input, 0, input.Length);
                        cs.FlushFinalBlock();
                    }
                    return ms.ToArray();
                }
            }
        }

        /// <summary>
        /// Decrypts a .nin file asynchronously.
        /// </summary>
        private async System.Threading.Tasks.Task<byte[]> DecryptNinFileAsync(string filePath)
        {
            byte[] input = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
            return await System.Threading.Tasks.Task.Run(() =>
            {
                using(PasswordDeriveBytes pdb = new PasswordDeriveBytes(Salt, FixedBytes))
                using(Aes aes = Aes.Create())
                {
                    aes.KeySize = 256;
                    aes.Key = pdb.GetBytes(32);
                    aes.IV = pdb.GetBytes(16);
                    using(MemoryStream ms = new MemoryStream())
                    {
                        using(CryptoStream cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
                        {
                            cs.Write(input, 0, input.Length);
                            cs.FlushFinalBlock();
                        }
                        return ms.ToArray();
                    }
                }
            });
        }

        /// <summary>
        /// Encrypts a PNG file into the .nin format.
        /// </summary>
        private async System.Threading.Tasks.Task EncryptPngToNinAsync(string source, string dest)
        {
            byte[] input = await File.ReadAllBytesAsync(source).ConfigureAwait(false);
            byte[] encrypted = await System.Threading.Tasks.Task.Run(() =>
            {
                using(PasswordDeriveBytes pdb = new PasswordDeriveBytes(Salt, FixedBytes))
                using(Aes aes = Aes.Create())
                {
                    aes.KeySize = 256;
                    aes.Key = pdb.GetBytes(32);
                    aes.IV = pdb.GetBytes(16);
                    using(MemoryStream ms = new MemoryStream())
                    {
                        using(CryptoStream cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                        {
                            cs.Write(input, 0, input.Length);
                            cs.FlushFinalBlock();
                        }
                        return ms.ToArray();
                    }
                }
            });
            await File.WriteAllBytesAsync(dest, encrypted).ConfigureAwait(false);
        }

        /// <summary>
        /// Converts raw decrypted bytes into a BitmapImage for the UI.
        /// </summary>
        public BitmapImage ConvertBytesToImage(byte[] imageBytes)
        {
            if(imageBytes == null || imageBytes.Length == 0)
                return null;
            try
            {
                using var ms = new MemoryStream(imageBytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze(); // Required for cross-thread access
                return bitmap;
            }
            catch { return null; }
        }
        #endregion

        #region TREE NAVIGATION
        /// <summary>
        /// Opens a folder dialog and populates the TreeView with folders and .nin files.
        /// </summary>
        private void BtnSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog();
            if(dialog.ShowDialog() == true)
            {
                TreeFolderStructure.Items.Clear();
                _selectedItems.Clear();
                _lastSelectedItem = null;
                var dirInfo = new DirectoryInfo(dialog.FolderName);

                // Add Subdirectories
                var dirs = dirInfo.GetDirectories().ToList();
                dirs.Sort(new NaturalDirectoryInfoComparer());
                foreach(var dir in dirs)
                    TreeFolderStructure.Items.Add(CreateTreeItem(dir));

                // Add Files in root
                var files = dirInfo.GetFiles("*.nin").ToList();
                files.Sort(new NaturalFileInfoComparer());
                foreach(var file in files)
                    TreeFolderStructure.Items.Add(new TreeViewItem { Header = file.Name, Tag = file.FullName });
            }
        }

        /// <summary>
        /// Recursively creates TreeView nodes for directory structures.
        /// </summary>
        private TreeViewItem CreateTreeItem(DirectoryInfo directoryInfo)
        {
            var node = new TreeViewItem { Header = directoryInfo.Name, IsExpanded = false, Tag = null };
            try
            {
                var dirs = directoryInfo.GetDirectories().ToList();
                dirs.Sort(new NaturalDirectoryInfoComparer());
                foreach(var dir in dirs)
                    node.Items.Add(CreateTreeItem(dir));

                var files = directoryInfo.GetFiles("*.nin").ToList();
                files.Sort(new NaturalFileInfoComparer());
                foreach(var file in files)
                    node.Items.Add(new TreeViewItem { Header = file.Name, Tag = file.FullName });
            }
            catch(UnauthorizedAccessException) { /* Ignore folders without permission */ }
            return node;
        }

        /// <summary>
        /// Finds the TreeViewItem parent for a given visual element (used in hit testing).
        /// </summary>
        private TreeViewItem GetNearestContainer(DependencyObject element)
        {
            while(element != null)
            {
                if(element is TreeViewItem tvi)
                    return tvi;
                if(element is TreeView)
                    return null;
                element = VisualTreeHelper.GetParent(element);
            }
            return null;
        }
        #endregion

        #region SELECTION & PREVIEW
        /// <summary>
        /// Handles custom selection logic (Shift/Ctrl) and triggers image preview.
        /// </summary>
        private async void TreeViewItem_OnSelected(object sender, RoutedEventArgs e)
        {
            var currentItem = e.OriginalSource as TreeViewItem;
            if(currentItem == null)
                return;

            // Handle Multi-selection modifiers
            if((Keyboard.Modifiers & ModifierKeys.Shift) > 0 && _lastSelectedItem != null)
                PerformShiftSelection(_lastSelectedItem, currentItem);
            else if((Keyboard.Modifiers & ModifierKeys.Control) > 0)
                ToggleSelection(currentItem);
            else
            {
                ClearAllSelections();
                ToggleSelection(currentItem);
            }

            _lastSelectedItem = currentItem;

            // Update Preview
            if(currentItem.Tag != null)
                await LoadPreviewAsync(currentItem.Tag.ToString());
            else
                ImagePreview.Source = null;

            e.Handled = true;
        }

        private void ToggleSelection(TreeViewItem item)
        {
            if(!_selectedItems.Contains(item))
            {
                _selectedItems.Add(item);
                item.Background = new SolidColorBrush(Color.FromRgb(58, 58, 58));
                item.Foreground = new SolidColorBrush(Color.FromRgb(255, 51, 60));
            }
        }

        private void ClearAllSelections()
        {
            foreach(var item in _selectedItems)
            {
                item.Background = Brushes.Transparent;
                item.ClearValue(Control.ForegroundProperty);
                item.IsSelected = false;
            }
            _selectedItems.Clear();
        }

        /// <summary>
        /// Selects a range of items between two points in the TreeView.
        /// </summary>
        private void PerformShiftSelection(TreeViewItem start, TreeViewItem end)
        {
            var allItems = GetVisibleItems(TreeFolderStructure);
            int startIndex = allItems.IndexOf(start);
            int endIndex = allItems.IndexOf(end);
            if(startIndex == -1 || endIndex == -1)
                return;

            for(int i = Math.Min(startIndex, endIndex);i <= Math.Max(startIndex, endIndex);i++)
                ToggleSelection(allItems[i]);
        }

        /// <summary>
        /// Flattens the visible TreeView hierarchy into a list for index-based selection.
        /// </summary>
        private List<TreeViewItem> GetVisibleItems(ItemsControl container)
        {
            var list = new List<TreeViewItem>();
            foreach(var item in container.Items)
            {
                var tvi = container.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
                if(tvi != null)
                {
                    list.Add(tvi);
                    if(tvi.IsExpanded)
                        list.AddRange(GetVisibleItems(tvi));
                }
            }
            return list;
        }

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
        #endregion

        #region EXPORT & REPLACE OPERATIONS
        /// <summary>
        /// Exports selected .nin files back to .png format.
        /// </summary>
        private async void BtnExportPng_Click(object sender, RoutedEventArgs e)
        {
            var filesToExport = _selectedItems.Where(i => i.Tag != null).Select(i => i.Tag.ToString()).ToList();
            if(!filesToExport.Any())
                return;

            if(filesToExport.Count == 1)
            {
                var saveDialog = new Microsoft.Win32.SaveFileDialog { Filter = "PNG Image|*.png", FileName = Path.GetFileNameWithoutExtension(filesToExport[0]) + ".png" };
                if(saveDialog.ShowDialog() == true)
                {
                    try
                    {
                        var data = await DecryptNinFileAsync(filesToExport[0]);
                        await File.WriteAllBytesAsync(saveDialog.FileName, data);
                    }
                    catch(Exception ex) { MessageBox.Show(ex.Message); }
                }
            }
            else
            {
                var folderDialog = new Microsoft.Win32.OpenFolderDialog();
                if(folderDialog.ShowDialog() == true)
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

        /// <summary>
        /// Replaces the currently selected .nin file with a chosen PNG.
        /// </summary>
        private void BtnReplaceWithPng_Click(object sender, RoutedEventArgs e)
        {
            if(_lastSelectedItem?.Tag == null)
                return;
            string targetNinPath = _lastSelectedItem.Tag.ToString();

            var openDialog = new Microsoft.Win32.OpenFileDialog { Filter = "PNG Image|*.png" };
            if(openDialog.ShowDialog() == true)
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
                    catch(Exception ex) { await Dispatcher.InvokeAsync(() => MessageBox.Show(ex.Message)); }
                });
            }
        }

        /// <summary>
        /// Batch replaces selected .nin files if a PNG with the same name exists in a target folder.
        /// </summary>
        private async void BtnSmartBatchReplace_Click(object sender, RoutedEventArgs e)
        {
            var selectedNinFiles = _selectedItems.Where(i => i.Tag != null && i.Tag.ToString().EndsWith(".nin")).ToList();
            if(!selectedNinFiles.Any())
                return;

            var folderDialog = new Microsoft.Win32.OpenFolderDialog();
            if(folderDialog.ShowDialog() == true)
            {
                var sourcePngs = Directory.GetFiles(folderDialog.FolderName, "*.png")
                    .ToDictionary(p => Path.GetFileNameWithoutExtension(p).ToLower(), p => p);

                int replacedCount = 0;
                var sem = new System.Threading.SemaphoreSlim(MaxParallelism);
                var tasks = selectedNinFiles.Select(async ninItem =>
                {
                    string fullPath = ninItem.Tag.ToString();
                    string name = Path.GetFileNameWithoutExtension(fullPath).ToLower();
                    if(sourcePngs.TryGetValue(name, out var pngPath))
                    {
                        await sem.WaitAsync();
                        try
                        { await EncryptPngToNinAsync(pngPath, fullPath); System.Threading.Interlocked.Increment(ref replacedCount); }
                        finally { sem.Release(); }
                    }
                });
                await System.Threading.Tasks.Task.WhenAll(tasks);
                MessageBox.Show($"Replaced {replacedCount} files.");
            }
        }

        /// <summary>
        /// Replaces all selected .nin files with a single selected PNG file.
        /// </summary>
        private async void BtnImportFromFile_Click(object sender, RoutedEventArgs e)
        {
            var selectedNinFiles = _selectedItems.Where(i => i.Tag != null && i.Tag.ToString().EndsWith(".nin")).ToList();
            if(!selectedNinFiles.Any())
                return;

            var openDialog = new Microsoft.Win32.OpenFileDialog { Filter = "PNG Image|*.png" };
            if(openDialog.ShowDialog() == true)
            {
                int count = 0;
                var sem = new System.Threading.SemaphoreSlim(MaxParallelism);
                var tasks = selectedNinFiles.Select(async item =>
                {
                    await sem.WaitAsync();
                    try
                    { await EncryptPngToNinAsync(openDialog.FileName, item.Tag.ToString()); System.Threading.Interlocked.Increment(ref count); }
                    finally { sem.Release(); }
                });
                await System.Threading.Tasks.Task.WhenAll(tasks);
                MessageBox.Show($"Imported to {count} files.");
            }
        }
        #endregion

        /// <summary>
        /// Opens external URLs in the default system browser.
        /// </summary>
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
            catch(Exception ex) { MessageBox.Show("Could not open the link: " + ex.Message); }
        }
    }
}

/// <summary>
/// Hi there, this is Vaenstormr.
/// Since I'm not a programmer and I mostly used AI to help me write this, please forgive any mistakes or inefficiencies.
/// I just wanted to say that you are free to use and modify this code as you see fit, and if you release it you keep it free for everyone, respecting the original license.
/// </summary>