# Concat Media page (WinUI)
This is a WinUI 3 page that provides an interface for merging media files of the same kind.

<img height="800" alt="image" src="https://github.com/user-attachments/assets/bd875e94-643c-4a58-90c5-57826175dc11" />

## How to use
Include this library into your WinUI solution and reference it in your WinUI project. Then navigate to the **ConcatMediaPage** when the user requests for it, passing a **ConcatProps** object as parameter. 
The **ConcatProps** object should contain the path to ffmpeg, the paths to the media files, and optionally, the full name of the page type to navigate back to when the user is done. If this last parameter is provided, you can get a list of the folders (containing split video) that was generated on the Concat Media page. If not, the user will be navigated back to whichever page called the Concat Media page and there'll be no parameters.
```
private void GoToConcat(){
  var ffmpegPath = Path.Join(Package.Current.InstalledLocation.Path, "Assets/ffmpeg.exe");
  var mediaPath = Path.Join(Package.Current.InstalledLocation.Path, "Assets/video1.mp4");
  var mediaPath2 = Path.Join(Package.Current.InstalledLocation.Path, "Assets/video2.mp4");
  Frame.Navigate(typeof(ConcatMediaPage), new ConcatProps { FfmpegPath = ffmpegPath, MediaPaths = [mediaPath, mediaPath2], TypeToNavigateTo = typeof(ThisPage).FullName});
}

protected override void OnNavigatedTo(NavigationEventArgs e)
{
    //outputFile is sent only if TypeToNavigateTo was specified in ConcatProps.
    if (e.Parameter is string outputFile)
    {
        Console.WriteLine($"Path of output file is {outputFile}");
    }
}
```

You may check out [ConcatMedia](https://github.com/PeteJobi/ConcatMedia) to see how a full application that uses this page.
