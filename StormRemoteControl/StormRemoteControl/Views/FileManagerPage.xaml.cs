using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace StormRemoteControl.Views
{
    public sealed partial class FileManagerPage : Page
    {
        private string _leftPath = "";
        private readonly ObservableCollection<FileItem> _leftItems = new();
        private readonly Stack<string> _leftHistory = new();

        // Russian format: space as thousands sep, comma as decimal
        private static readonly CultureInfo RuCulture = new("ru-RU");

        public FileManagerPage()
        {
            this.InitializeComponent();
            LeftFileList.ItemsSource = _leftItems;
            RegisterKeyboardAccelerators();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            BuildQuickAccess();
            NavigateLeft(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        }

        // ═══════════════════════════════════════════════════════════
        //  KEYBOARD ACCELERATORS — registered on the Page
        // ═══════════════════════════════════════════════════════════

        private void RegisterKeyboardAccelerators()
        {
            // F3 — Preview
            var f3 = new KeyboardAccelerator { Key = Windows.System.VirtualKey.F3 };
            f3.Invoked += (s, e) => { OnHotkeyF3(null!, null!); e.Handled = true; };
            this.KeyboardAccelerators.Add(f3);

            // F5 — Copy to remote
            var f5 = new KeyboardAccelerator { Key = Windows.System.VirtualKey.F5 };
            f5.Invoked += (s, e) => { OnHotkeyF5(null!, null!); e.Handled = true; };
            this.KeyboardAccelerators.Add(f5);

            // F6 — Move
            var f6 = new KeyboardAccelerator { Key = Windows.System.VirtualKey.F6 };
            f6.Invoked += (s, e) => { OnHotkeyF6(null!, null!); e.Handled = true; };
            this.KeyboardAccelerators.Add(f6);

            // F7 — New folder
            var f7 = new KeyboardAccelerator { Key = Windows.System.VirtualKey.F7 };
            f7.Invoked += (s, e) => { OnNewFolderClicked(null!, null!); e.Handled = true; };
            this.KeyboardAccelerators.Add(f7);

            // F8 — Delete
            var f8 = new KeyboardAccelerator { Key = Windows.System.VirtualKey.F8 };
            f8.Invoked += (s, e) => { OnDeleteClicked(null!, null!); e.Handled = true; };
            this.KeyboardAccelerators.Add(f8);

            // Delete key
            var del = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Delete };
            del.Invoked += (s, e) => { OnDeleteClicked(null!, null!); e.Handled = true; };
            this.KeyboardAccelerators.Add(del);

            // F9 — Rename
            var f9 = new KeyboardAccelerator { Key = Windows.System.VirtualKey.F9 };
            f9.Invoked += (s, e) => { OnHotkeyF9(null!, null!); e.Handled = true; };
            this.KeyboardAccelerators.Add(f9);

            // Ctrl+R — Refresh
            var ctrlR = new KeyboardAccelerator
            {
                Key = Windows.System.VirtualKey.R,
                Modifiers = Windows.System.VirtualKeyModifiers.Control
            };
            ctrlR.Invoked += (s, e) => { OnRefreshClicked(null!, null!); e.Handled = true; };
            this.KeyboardAccelerators.Add(ctrlR);

            // Escape — Go back
            var esc = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
            esc.Invoked += (s, e) => { OnBackClicked(null!, null!); e.Handled = true; };
            this.KeyboardAccelerators.Add(esc);
        }

        // ═══════════════════════════════════════════════════════════
        //  HOTKEY BAR CLICK HANDLERS
        // ═══════════════════════════════════════════════════════════

        private void OnHotkeyF3(object sender, RoutedEventArgs e)
        {
            if (LeftFileList.SelectedItem is FileItem item && !item.IsDirectory)
                OpenFile(item.FullPath);
            else
                StatusText.Text = "Выберите файл для просмотра";
        }

        private void OnHotkeyF5(object sender, RoutedEventArgs e)
            => OnCopyToRemoteClicked(null!, null!);

        private void OnHotkeyF6(object sender, RoutedEventArgs e)
        {
            var selected = LeftFileList.SelectedItems.OfType<FileItem>().ToList();
            if (selected.Count == 0) { StatusText.Text = "Выберите файлы для перемещения"; return; }
            StatusText.Text = $"Перемещение {selected.Count} файлов на удалённый ПК (в разработке)";
        }

        private void OnHotkeyF9(object sender, RoutedEventArgs e)
            => OnRenameClicked();

        // ═══════════════════════════════════════════════════════════
        //  NAVIGATION
        // ═══════════════════════════════════════════════════════════

        private void NavigateLeft(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(_leftPath)) _leftHistory.Push(_leftPath);
                _leftPath = path;
                LeftPathBox.Text = path;
                LoadLeftDirectory(path);
            }
            catch (Exception ex) { StatusText.Text = $"Ошибка: {ex.Message}"; }
        }

        private void LoadLeftDirectory(string path)
        {
            _leftItems.Clear();
            try
            {
                var di = new DirectoryInfo(path);
                foreach (var dir in di.EnumerateDirectories()
                    .Where(d => (d.Attributes & FileAttributes.System) == 0)
                    .OrderBy(d => d.Name))
                {
                    try
                    {
                        _leftItems.Add(new FileItem
                        {
                            Name = dir.Name, FullPath = dir.FullName, IsDirectory = true,
                            DateModified = dir.LastWriteTime, SizeBytes = -1,
                            IconGlyph = "\uE8B7",
                            IconColor = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 183, 77))
                        });
                    }
                    catch { }
                }

                foreach (var file in di.EnumerateFiles()
                    .Where(f => (f.Attributes & FileAttributes.System) == 0)
                    .OrderBy(f => f.Name))
                {
                    try
                    {
                        _leftItems.Add(new FileItem
                        {
                            Name = file.Name, FullPath = file.FullName, IsDirectory = false,
                            DateModified = file.LastWriteTime, SizeBytes = file.Length,
                            IconGlyph = GetFileIcon(file.Extension),
                            IconColor = GetFileIconColor(file.Extension)
                        });
                    }
                    catch { }
                }

                StatusText.Text = path;
                ItemCountText.Text = $"{_leftItems.Count} объектов";
                SelectedCountText.Text = $"{_leftItems.Count} объектов";
                UpdateFreeSpace(path);

                // Calculate folder sizes in background
                _ = CalculateFolderSizesAsync();
            }
            catch (UnauthorizedAccessException) { StatusText.Text = "Доступ запрещён"; }
            catch (Exception ex) { StatusText.Text = $"Ошибка: {ex.Message}"; }
        }

        /// <summary>Calculates folder sizes in background and updates UI.</summary>
        private async Task CalculateFolderSizesAsync()
        {
            var folders = _leftItems.Where(i => i.IsDirectory).ToList();
            foreach (var folder in folders)
            {
                try
                {
                    long size = await Task.Run(() => GetDirectorySize(folder.FullPath));
                    if (size >= 0)
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            folder.SizeBytes = size;
                            folder.NotifySizeChanged();
                        });
                    }
                }
                catch { }
            }
        }

        private static long GetDirectorySize(string path)
        {
            try
            {
                long total = 0;
                var di = new DirectoryInfo(path);
                foreach (var file in di.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    try { total += file.Length; }
                    catch { }
                }
                return total;
            }
            catch { return -1; }
        }

        // ═══════════════════════════════════════════════════════════
        //  QUICK ACCESS
        // ═══════════════════════════════════════════════════════════

        private void BuildQuickAccess()
        {
            LeftQuickAccess.Children.Clear();
            AddQuickBtn("\uE80F", "Рабочий стол", Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            AddQuickBtn("\uE896", "Загрузки", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
            AddQuickBtn("\uE8A5", "Документы", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            AddQuickBtn("\uEB9F", "Изображения", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
            AddQuickBtn("\uE8D6", "Музыка", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
            AddQuickBtn("\uE714", "Видео", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));

            try
            {
                foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                {
                    string label = string.IsNullOrEmpty(drive.VolumeLabel)
                        ? drive.Name.TrimEnd('\\') : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
                    AddQuickBtn("\uEDA2", label, drive.Name);
                }
            }
            catch { }
        }

        private void AddQuickBtn(string glyph, string label, string path)
        {
            if (!Directory.Exists(path) && !Path.IsPathRooted(path)) return;

            var btn = new Button
            {
                Padding = new Thickness(8, 4, 8, 4),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderBrush = (SolidColorBrush)Application.Current.Resources["BorderBrush"],
                BorderThickness = new Thickness(1),
                Tag = path
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            sp.Children.Add(new FontIcon
            {
                FontFamily = new FontFamily("Segoe MDL2 Assets"), Glyph = glyph, FontSize = 11,
                Foreground = (SolidColorBrush)Application.Current.Resources["AccentColorBrush"]
            });
            sp.Children.Add(new TextBlock
            {
                Text = label, FontFamily = new FontFamily("Century Gothic"),
                FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center
            });
            btn.Content = sp;
            btn.Click += (s, e) =>
            {
                if (s is Button b && b.Tag is string p && Directory.Exists(p)) NavigateLeft(p);
            };
            LeftQuickAccess.Children.Add(btn);
        }

        // ═══════════════════════════════════════════════════════════
        //  EVENT HANDLERS
        // ═══════════════════════════════════════════════════════════

        private void OnBackClicked(object sender, RoutedEventArgs e)
        {
            if (this.Frame.CanGoBack) this.Frame.GoBack();
        }

        private void OnRefreshClicked(object sender, RoutedEventArgs e) => LoadLeftDirectory(_leftPath);

        private void OnLeftUpClicked(object sender, RoutedEventArgs e)
        {
            var parent = Directory.GetParent(_leftPath);
            if (parent != null) NavigateLeft(parent.FullName);
        }

        private void OnLeftHomeClicked(object sender, RoutedEventArgs e)
            => NavigateLeft(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));

        private void OnLeftPathKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                string path = LeftPathBox.Text.Trim();
                if (Directory.Exists(path)) NavigateLeft(path);
                else StatusText.Text = $"Путь не найден: {path}";
            }
        }

        private void OnLeftDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (LeftFileList.SelectedItem is FileItem item)
            {
                if (item.IsDirectory) NavigateLeft(item.FullPath);
                else OpenFile(item.FullPath);
            }
        }

        private void OnLeftRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (LeftFileList.SelectedItem is FileItem item)
                ShowContextMenu(item, e.GetPosition(LeftFileList));
        }

        // ═══════════════════════════════════════════════════════════
        //  DRAG & DROP
        // ═══════════════════════════════════════════════════════════

        private void OnLeftDragStart(object sender, DragItemsStartingEventArgs e)
        {
            var paths = new List<string>();
            foreach (var item in e.Items.OfType<FileItem>())
                paths.Add(item.FullPath);
            e.Data.SetText(string.Join("\n", paths));
            e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        }

        private void OnLeftDrop(object sender, DragEventArgs e)
        {
            StatusText.Text = "Приём файлов из удалённого ПК (в разработке)";
            e.Handled = true;
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Копировать сюда";
            e.DragUIOverride.IsCaptionVisible = true;
        }

        // ═══════════════════════════════════════════════════════════
        //  TRANSFER BUTTONS
        // ═══════════════════════════════════════════════════════════

        private void OnCopyToRemoteClicked(object sender, RoutedEventArgs e)
        {
            var selected = LeftFileList.SelectedItems.OfType<FileItem>().ToList();
            if (selected.Count == 0) { StatusText.Text = "Выберите файлы для копирования"; return; }
            StatusText.Text = $"Копирование {selected.Count} файлов на удалённый ПК (в разработке)";
        }

        private void OnCopyFromRemoteClicked(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "Копирование с удалённого ПК (в разработке)";
        }

        private async void OnNewFolderClicked(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "НОВАЯ ПАПКА", PrimaryButtonText = "СОЗДАТЬ", CloseButtonText = "ОТМЕНА",
                XamlRoot = this.XamlRoot, RequestedTheme = ElementTheme.Dark
            };
            var nameBox = new TextBox
            {
                PlaceholderText = "Имя папки", FontFamily = new FontFamily("Century Gothic"),
                FontWeight = Microsoft.UI.Text.FontWeights.Bold
            };
            dialog.Content = nameBox;

            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(nameBox.Text))
            {
                try
                {
                    Directory.CreateDirectory(Path.Combine(_leftPath, nameBox.Text.Trim()));
                    LoadLeftDirectory(_leftPath);
                    StatusText.Text = $"Папка \"{nameBox.Text.Trim()}\" создана";
                }
                catch (Exception ex) { StatusText.Text = $"Ошибка: {ex.Message}"; }
            }
        }

        private async void OnDeleteClicked(object sender, RoutedEventArgs e)
        {
            var selected = LeftFileList.SelectedItems.OfType<FileItem>().ToList();
            if (selected.Count == 0) { StatusText.Text = "Выберите файлы для удаления"; return; }

            var dialog = new ContentDialog
            {
                Title = "ПОДТВЕРЖДЕНИЕ УДАЛЕНИЯ",
                Content = $"Удалить {selected.Count} объект(ов)?",
                PrimaryButtonText = "УДАЛИТЬ", CloseButtonText = "ОТМЕНА",
                XamlRoot = this.XamlRoot, RequestedTheme = ElementTheme.Dark
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                int deleted = 0;
                foreach (var item in selected)
                {
                    try
                    {
                        if (item.IsDirectory) Directory.Delete(item.FullPath, true);
                        else File.Delete(item.FullPath);
                        deleted++;
                    }
                    catch { }
                }
                LoadLeftDirectory(_leftPath);
                StatusText.Text = $"Удалено: {deleted} объектов";
            }
        }

        private async void OnRenameClicked()
        {
            if (LeftFileList.SelectedItem is not FileItem item) return;
            var dialog = new ContentDialog
            {
                Title = "ПЕРЕИМЕНОВАТЬ", PrimaryButtonText = "ПРИМЕНИТЬ", CloseButtonText = "ОТМЕНА",
                XamlRoot = this.XamlRoot, RequestedTheme = ElementTheme.Dark
            };
            var nameBox = new TextBox
            {
                Text = item.Name, FontFamily = new FontFamily("Century Gothic"),
                FontWeight = Microsoft.UI.Text.FontWeights.Bold
            };
            dialog.Content = nameBox;

            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(nameBox.Text))
            {
                try
                {
                    string newPath = Path.Combine(Path.GetDirectoryName(item.FullPath)!, nameBox.Text.Trim());
                    if (item.IsDirectory) Directory.Move(item.FullPath, newPath);
                    else File.Move(item.FullPath, newPath);
                    LoadLeftDirectory(_leftPath);
                    StatusText.Text = $"Переименовано: {nameBox.Text.Trim()}";
                }
                catch (Exception ex) { StatusText.Text = $"Ошибка: {ex.Message}"; }
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  CONTEXT MENU
        // ═══════════════════════════════════════════════════════════

        private void ShowContextMenu(FileItem item, Windows.Foundation.Point pos)
        {
            var flyout = new MenuFlyout();
            flyout.Items.Add(MakeMenuItem("\uE8AC", "ОТКРЫТЬ", () =>
            {
                if (item.IsDirectory) NavigateLeft(item.FullPath);
                else OpenFile(item.FullPath);
            }));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(MakeMenuItem("\uE8C8", "КОПИРОВАТЬ ПУТЬ", () =>
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(item.FullPath);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                StatusText.Text = "Путь скопирован";
            }));
            flyout.Items.Add(MakeMenuItem("\uE8AC", "ПЕРЕИМЕНОВАТЬ (F9)", () => OnRenameClicked()));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(MakeMenuItem("\uE74D", "УДАЛИТЬ (F8)", () => OnDeleteClicked(null!, null!)));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(MakeMenuItem("\uE946", "СВОЙСТВА", async () =>
            {
                string info = item.IsDirectory
                    ? $"Тип: Папка\nРазмер: {item.SizeDisplay}\nПуть: {item.FullPath}\nИзменён: {item.DateModified:dd.MM.yyyy HH:mm}"
                    : $"Тип: {Path.GetExtension(item.FullPath).ToUpper()} файл\nРазмер: {item.SizeDisplay}\nПуть: {item.FullPath}\nИзменён: {item.DateModified:dd.MM.yyyy HH:mm}";

                await new ContentDialog
                {
                    Title = item.Name.ToUpper(), Content = info, CloseButtonText = "ОК",
                    XamlRoot = this.XamlRoot, RequestedTheme = ElementTheme.Dark
                }.ShowAsync();
            }));
            flyout.ShowAt(LeftFileList, pos);
        }

        private static MenuFlyoutItem MakeMenuItem(string glyph, string text, Action action)
        {
            var item = new MenuFlyoutItem
            {
                Text = text,
                Icon = new FontIcon { Glyph = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets") }
            };
            item.Click += (s, e) => action();
            return item;
        }

        // ═══════════════════════════════════════════════════════════
        //  HELPERS
        // ═══════════════════════════════════════════════════════════

        private static void OpenFile(string path)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                    { UseShellExecute = true });
            }
            catch { }
        }

        private void UpdateFreeSpace(string path)
        {
            try
            {
                string root = Path.GetPathRoot(path) ?? "";
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                    FreeSpaceText.Text = $"Свободно: {FormatSize(drive.AvailableFreeSpace)} / {FormatSize(drive.TotalSize)}";
            }
            catch { }
        }

        /// <summary>Format bytes in Russian format: "2 100,00 KB" with space thousands separator.</summary>
        internal static string FormatSize(long bytes)
        {
            if (bytes < 0) return "";
            if (bytes < 1024) return FormatNumber((double)bytes) + " B";

            double kb = bytes / 1024.0;
            if (kb < 1024) return FormatNumber(kb) + " KB";

            double mb = kb / 1024.0;
            if (mb < 1024) return FormatNumber(mb) + " MB";

            double gb = mb / 1024.0;
            if (gb < 1024) return FormatNumber(gb) + " GB";

            double tb = gb / 1024.0;
            return FormatNumber(tb) + " TB";
        }

        /// <summary>Format number: "124 223,21" — space as thousands, comma as decimal.</summary>
        private static string FormatNumber(double value)
        {
            // Use Russian culture: space = thousands sep, comma = decimal sep
            return value.ToString("N2", RuCulture);
        }

        private static string GetFileIcon(string ext) => ext.ToLowerInvariant() switch
        {
            ".txt" or ".log" or ".md" or ".csv" => "\uE8A5",
            ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".webp" or ".svg" => "\uEB9F",
            ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" => "\uE714",
            ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" => "\uE8D6",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "\uF012",
            ".exe" or ".msi" => "\uE756",
            ".dll" or ".sys" => "\uE74C",
            ".pdf" => "\uEA90",
            ".cs" or ".js" or ".py" or ".html" or ".css" or ".json" or ".xml" => "\uE943",
            _ => "\uE7C3"
        };

        private static SolidColorBrush GetFileIconColor(string ext) => ext.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" => new(ColorHelper.FromArgb(255, 129, 199, 132)),
            ".mp4" or ".avi" or ".mkv" => new(ColorHelper.FromArgb(255, 100, 181, 246)),
            ".mp3" or ".wav" or ".flac" => new(ColorHelper.FromArgb(255, 206, 147, 216)),
            ".zip" or ".rar" or ".7z" => new(ColorHelper.FromArgb(255, 255, 183, 77)),
            ".exe" or ".msi" => new(ColorHelper.FromArgb(255, 211, 47, 47)),
            ".cs" or ".js" or ".py" or ".html" or ".css" => new(ColorHelper.FromArgb(255, 16, 185, 129)),
            _ => new(ColorHelper.FromArgb(180, 255, 255, 255))
        };
    }

    public class FileItem : System.ComponentModel.INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public string FullPath { get; set; } = "";
        public bool IsDirectory { get; set; }
        private long _sizeBytes;
        public long SizeBytes
        {
            get => _sizeBytes;
            set { _sizeBytes = value; PropertyChanged?.Invoke(this, new(nameof(SizeDisplay))); }
        }
        public DateTime DateModified { get; set; }
        public string IconGlyph { get; set; } = "\uE7C3";
        public SolidColorBrush IconColor { get; set; } = new(ColorHelper.FromArgb(180, 255, 255, 255));
        public string SizeDisplay => SizeBytes < 0 ? "" : FileManagerPage.FormatSize(SizeBytes);
        public string DateDisplay => DateModified.ToString("dd.MM.yyyy HH:mm");

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public void NotifySizeChanged() => PropertyChanged?.Invoke(this, new(nameof(SizeDisplay)));
    }
}
