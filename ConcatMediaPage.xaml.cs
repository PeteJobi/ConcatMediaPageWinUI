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
using WinUIShared.Enums;

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
        private string? outputFile;
        public MainModel viewModel = new() { Items = [] };
        private readonly ConcatProcessor concatProcessor = new();
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
            ProcessProgress.LeftTextPrimary = string.Empty;
            ProcessProgress.RightTextPrimary = string.Empty;
            ProcessProgress.CenterTextSecondary = string.Empty;
            ProcessProgress.ProgressPrimary = 0;
            ProcessProgress.ProgressSecondary = 0;

            var paths = viewModel.Items.Select(i => i.FilePath).ToArray();
            viewModel.State = OperationState.DuringOperation;

            var fileProgress = new Progress<FileProgress>(progress =>
            {
                if (progress.TotalRangeCount != null) ProcessProgress.LeftTextPrimary = progress.TotalRangeCount;
                if (progress.CurrentRangeFileName != null)
                    ProcessProgress.RightTextPrimary = progress.CurrentRangeFileName;
            });
            var valueProgress = new Progress<ValueProgress>(progress =>
            {
                ProcessProgress.ProgressPrimary = progress.OverallProgress;
                ProcessProgress.ProgressSecondary = progress.CurrentActionProgress;
                ProcessProgress.CenterTextSecondary = progress.CurrentActionProgressText;
            });
            var failed = false;
            string? errorMessage = null;

            string? tempOutputFile = null;
            outputFile = null;
            try
            {
                await concatProcessor.Concat(ffmpegPath, paths, fileProgress, valueProgress, SetOutputFile, ErrorActionFromFfmpeg);

                if (viewModel.State == OperationState.BeforeOperation) return; //Canceled
                if (failed)
                {
                    viewModel.State = OperationState.BeforeOperation;
                    await ErrorAction(errorMessage!);
                    await concatProcessor.Cancel();
                    return;
                }

                viewModel.State = OperationState.AfterOperation;
                ProcessProgress.RightTextPrimary = "Done";
                outputFile = tempOutputFile;
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
                tempOutputFile = file;
            }

            async Task ErrorAction(string message)
            {
                ErrorDialog.Title = "Concat operation failed";
                ErrorDialog.Content = message;
                await ErrorDialog.ShowAsync();
            }
        }

        private void ProcessProgress_OnPauseRequested(object? sender, EventArgs e) => concatProcessor.Pause();

        private void ProcessProgress_OnResumeRequested(object? sender, EventArgs e) => concatProcessor.Resume();

        private void ProcessProgress_OnViewRequested(object? sender, EventArgs e) => concatProcessor.ViewFile(outputFile);

        private async void ProcessProgress_OnCancelRequested(object? sender, EventArgs e)
        {
            await concatProcessor.Cancel();
            viewModel.State = OperationState.BeforeOperation;
        }

        private void ProcessProgress_OnCloseRequested(object? sender, EventArgs e) => viewModel.State = OperationState.BeforeOperation;

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
