using System.Text.Json;
using DevHub.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DevHub.App;

public sealed partial class MainWindow
{
    private async Task ValidateSmokeLayoutAsync()
    {
        var results = new List<object>();
        foreach (var maximized in new[] { false, true, false })
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter) { if (maximized) presenter.Maximize(); else presenter.Restore(); }
            await Task.Delay(250);
            foreach (var page in new[] { "home", "tools", "agents", "versions", "projects", "diagnostics", "settings", "tasks" })
            {
                Navigate(page); Root.UpdateLayout(); await Task.Delay(80);
                var position = PageContent.TransformToVisual(PageScroll).TransformPoint(new Windows.Foundation.Point(0, 0));
                var width = PageContent.ActualWidth; var viewport = PageScroll.ActualWidth;
                if (width <= 0 || viewport <= 0 || position.X < -1 || position.X + width > viewport + 1 || PageScroll.HorizontalOffset > 0.5)
                    throw new InvalidOperationException($"页面布局超出可视区域：{page}，maximized={maximized}，x={position.X}，width={width}，viewport={viewport}");
                results.Add(new { Page = page, Maximized = maximized, X = position.X, Width = width, Viewport = viewport });
                if (maximized && page is "home" or "settings") { await Task.Delay(250); await SaveSmokeImageAsync(page); }
            }
        }
        await File.WriteAllTextAsync(Path.Combine(vm.Paths.Logs, "layout-checks.json"), JsonSerializer.Serialize(results, JsonDefaults.Options));
    }
    private async Task SaveSmokeImageAsync(string page)
    {
        try
        {
            var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(Root);
            var buffer = await bitmap.GetPixelsAsync(); var bytes = new byte[buffer.Length];
            using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
            var file = Path.Combine(vm.Paths.Logs, "smoke-layout-" + page + ".png");
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes); await encoder.FlushAsync();
            using var encoded = new DataReader(output.GetInputStreamAt(0)); await encoded.LoadAsync((uint)output.Size);
            var png = new byte[output.Size]; encoded.ReadBytes(png); await File.WriteAllBytesAsync(file, png);
        }
        catch (Exception error) { await File.AppendAllTextAsync(Path.Combine(vm.Paths.Logs, "smoke-render.log"), error.GetType().Name + "\n"); }
    }
}
