using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage.Pickers;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace ConcatMediaPage
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class ConcatMediaPage : Page
    {
        private string? navigateTo;
        private string ffmpegPath;
        private string outputFile;
        public MainModel viewModel = new() { Items = [] };
        private readonly ConcatProcessor concatProcessor = new();
        private readonly double progressMax = 1_000_000;
        public static List<string> AllSupportedTypes = [".mkv", ".mp4", ".mp3", ".wav"];

        public ConcatMediaPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            if (e.Parameter is ConcatProps props)
            {
                navigateTo = props.TypeToNavigateTo;
                ffmpegPath = props.FfmpegPath;
                await AddMedia(props.MediaPaths.ToArray());
            }
        }

        private async Task AddMedia(string[] paths)
        {
            Array.Sort(paths);
            foreach (var path in paths)
            {
                viewModel.Items.Add(new ConcatItem
                {
                    FilePath = path,
                    FileName = Path.GetFileName(path)
                });
            }
        }

        private async void MainPage_OnDrop(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                await AddMedia(items.Select(i => i.Path).ToArray());
            }
        }

        private void MainPage_OnDragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }

        private async void ShowFilePicker(object sender, RoutedEventArgs e)
        {
            var filePicker = new FileOpenPicker();
            filePicker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
            AllSupportedTypes.ForEach(t => filePicker.FileTypeFilter.Add(t));
            var windowId = XamlRoot?.ContentIslandEnvironment?.AppWindowId;
            var hwnd = Win32Interop.GetWindowFromWindowId(windowId.Value);
            WinRT.Interop.InitializeWithWindow.Initialize(filePicker, hwnd);
            var files = await filePicker.PickMultipleFilesAsync();
            await AddMedia(files.Select(f => f.Path).ToArray());
        }

        private void RemoveAllMedia(object sender, RoutedEventArgs e)
        {
            viewModel.Items.Clear();
            if (viewModel.AfterOperation) viewModel.State = OperationState.BeforeOperation;
        }

        private void DeleteItemClicked(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            var item = (ConcatItem)button.DataContext;
            viewModel.Items.Remove(item);
        }

        private async void ProcessConcat(object sender, RoutedEventArgs e)
        {
            if (viewModel.Items.Count < 2) return;
            TotalSegmentCount.Text = string.Empty;
            CurrentSegmentFileName.Text = string.Empty;
            CurrentConcatProgressText.Text = string.Empty;
            OverallConcatProgress.Value = 0;
            CurrentConcatProgress.Value = 0;

            var paths = viewModel.Items.Select(i => i.FilePath).ToArray();
            viewModel.State = OperationState.DuringOperation;

            var fileProgress = new Progress<FileProgress>(progress =>
            {
                if (progress.TotalRangeCount != null) TotalSegmentCount.Text = progress.TotalRangeCount;
                if (progress.CurrentRangeFileName != null)
                    CurrentSegmentFileName.Text = progress.CurrentRangeFileName;
            });
            var valueProgress = new Progress<ValueProgress>(progress =>
            {
                OverallConcatProgress.Value = progress.OverallProgress;
                CurrentConcatProgress.Value = progress.CurrentActionProgress;
                CurrentConcatProgressText.Text = progress.CurrentActionProgressText;
            });
            var failed = false;
            string? errorMessage = null;

            try
            {
                await concatProcessor.Concat(ffmpegPath, paths, progressMax, fileProgress, valueProgress, SetOutputFile, ErrorActionFromFfmpeg);

                if (viewModel.State == OperationState.BeforeOperation) return; //Canceled
                if (failed)
                {
                    viewModel.State = OperationState.BeforeOperation;
                    await ErrorAction(errorMessage!);
                    await concatProcessor.Cancel();
                    return;
                }

                viewModel.State = OperationState.AfterOperation;
                CurrentSegmentFileName.Text = "Done";
            }
            catch (Exception ex)
            {
                await ErrorAction(ex.Message);
                viewModel.State = OperationState.BeforeOperation;
            }

            void ErrorActionFromFfmpeg(string message)
            {
                failed = true;
                errorMessage = message;
            }

            void SetOutputFile(string file)
            {
                outputFile = file;
            }

            async Task ErrorAction(string message)
            {
                ErrorDialog.Title = "Concat operation failed";
                ErrorDialog.Content = message;
                await ErrorDialog.ShowAsync();
            }
        }

        private void PauseOrViewConcat_OnClick(object sender, RoutedEventArgs e)
        {
            if (viewModel.State == OperationState.AfterOperation)
            {
                concatProcessor.ViewFiles(outputFile);
                return;
            }

            if (viewModel.ProcessPaused)
            {
                concatProcessor.Resume();
                viewModel.ProcessPaused = false;
            }
            else
            {
                concatProcessor.Pause();
                viewModel.ProcessPaused = true;
            }
        }

        private void CancelOrCloseConcat_OnClick(object sender, RoutedEventArgs e)
        {
            if (viewModel.State == OperationState.AfterOperation)
            {
                viewModel.State = OperationState.BeforeOperation;
                return;
            }

            FlyoutBase.ShowAttachedFlyout((FrameworkElement)sender);
        }

        private async void CancelProcess(object sender, RoutedEventArgs e)
        {
            await concatProcessor.Cancel();
            viewModel.State = OperationState.BeforeOperation;
            viewModel.ProcessPaused = false;
            CancelFlyout.Hide();
        }

        private void GoBack(object sender, RoutedEventArgs e)
        {
            _ = concatProcessor.Cancel();
            if (navigateTo == null) Frame.GoBack();
            else Frame.NavigateToType(Type.GetType(navigateTo), outputFile, new FrameNavigationOptions { IsNavigationStackEnabled = false });
        }
    }

    public class ConcatProps
    {
        public string FfmpegPath { get; set; }
        public IEnumerable<string> MediaPaths { get; set; }
        public string? TypeToNavigateTo { get; set; }
    }
}
