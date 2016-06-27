using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Runtime;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

using ImageResizer;
using Microsoft.Win32;
using PhotoSauce.MagicScaler;

static class MathUtil
{
	private static double StandardDeviation(this IEnumerable<double> vals)
	{
		double mean = vals.Average();
		double variance = vals.Select(val => (val - mean) * (val - mean)).Sum();
		return Math.Sqrt(variance / vals.Count());
	}
}

public partial class MainWindow : Window
{
	private double ticksToMs(long ticks) => (double)ticks / Stopwatch.Frequency * 1000;

	public MainWindow()
	{
		InitializeComponent();
	}

	private void Window_Loaded(object sender, RoutedEventArgs e)
	{
		//RunTests(@"c:\witch.jpg");
	}

	private void MenuItem_Click(object sender, RoutedEventArgs e)
	{
		var ofd = new OpenFileDialog() { Filter = "Image files | *.png; *.jpeg; *.jpg; *.bmp; *.tif; *.tiff; *.gif | All files(*.*) | *.* " };
		if (ofd.ShowDialog() == true)
			RunTests(ofd.FileName);
	}

	private void runTest(Func<Stream> test, string label, Label uiLabel, bool breakup)
	{
		int iterations = 10;
		int parallel1 = 4;
		int parallel2 = 8;
		var tasks = new List<Task>();
		var times = new ConcurrentStack<double>();
		var elapsed = new Stopwatch();

		if (breakup)
		{
			GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
			GC.Collect(2, GCCollectionMode.Forced, true);
			Thread.Sleep(1000);
		}

		var tests = new List<Action>();
		tests.AddRange(Enumerable.Repeat<Action>(() => { var sw = new Stopwatch(); sw.Start(); test(); sw.Stop(); times.Push(ticksToMs(sw.ElapsedTicks)); }, iterations));
		foreach (var action in tests.Take(10))
			Task.Run(action).Wait();
		uiLabel.Content = $"{label}:\nSerial x{times.Count}: {times.Average():f2}ms ±{times.StandardDeviation():f2}ms";

		times.Clear();
		elapsed.Restart();
		tasks.Clear();
		foreach (var action in tests.Take(parallel1))
			tasks.Add(Task.Run(action));
		Task.WaitAll(tasks.ToArray());
		elapsed.Stop();
		uiLabel.Content += $"\nParallel x{times.Count}: {times.Average():f2}ms ±{times.StandardDeviation():f2}ms ({ticksToMs(elapsed.ElapsedTicks):f2} elapsed)";

		times.Clear();
		elapsed.Restart();
		tasks.Clear();
		foreach (var action in tests.Take(parallel2))
			tasks.Add(Task.Run(action));
		Task.WaitAll(tasks.ToArray());
		elapsed.Stop();
		uiLabel.Content += $"\nParallel x{times.Count}: {times.Average():f2}ms ±{times.StandardDeviation():f2}ms ({ticksToMs(elapsed.ElapsedTicks):f2} elapsed)";
	}

	private void RunTests(string inFile)
	{
		bool breakup = false;
		var inFileInfo = new FileInfo(inFile);

		var settings = new ProcessImageSettings {
			Width = 400,
			Height = 0,
			Sharpen = false,
			JpegQuality = 90,
			ResizeMode = CropScaleMode.Crop,
			SaveFormat = FileFormat.Jpeg,
			//BlendingMode = GammaMode.sRGB,
			//HybridMode = HybridScaleMode.Turbo,
			//Interpolation = InterpolationSettings.Cubic,
			//MatteColor = Color.Pink,
			//Anchor = CropAnchor.Bottom | CropAnchor.Right,
		};

		var inImage = File.ReadAllBytes(inFileInfo.FullName);

		new ImageResizer.Plugins.FastScaling.FastScalingPlugin().Install(ImageResizer.Configuration.Config.Current);
		//new ImageResizer.Plugins.PrettyGifs.PrettyGifs().Install(ImageResizer.Configuration.Config.Current);

		int speed = settings.HybridMode == HybridScaleMode.Off ? -2 : settings.HybridMode == HybridScaleMode.FavorQuality ? 0 : settings.HybridMode == HybridScaleMode.FavorSpeed ? 2 : 4;
		//string filter = settings.Interpolation.Equals(InterpolationSettings.Cubic) ? "cubic" : settings.Interpolation.Equals(InterpolationSettings.Linear) ? "linear" : "";
		string anchor1 = (settings.Anchor & CropAnchor.Top) == CropAnchor.Top ? "top" : (settings.Anchor & CropAnchor.Bottom) == CropAnchor.Bottom ? "bottom" : "middle";
		string anchor2 = (settings.Anchor & CropAnchor.Left) == CropAnchor.Left ? "left" : (settings.Anchor & CropAnchor.Right) == CropAnchor.Right ? "right" : "center";
		string bgcolor = settings.MatteColor == Color.Empty ? null : $"&bgcolor={settings.MatteColor.ToKnownColor()}";
		string quality = settings.JpegQuality == 0 ? null : $"&quality={settings.JpegQuality}";
		string format = settings.SaveFormat == FileFormat.Png8 ? "gif" : settings.SaveFormat.ToString();
		var irs = new ResizeSettings($"width={settings.Width}&height={settings.Height}&mode={settings.ResizeMode}&anchor={anchor1}{anchor2}&autorotate=true{bgcolor}&scale=both&format={format}&quality={settings.JpegQuality}&fastscale=true&down.speed={speed}");

		Func<Stream> gdi = () => { using (var msi = new MemoryStream(inImage)) { var mso = new MemoryStream(16384); GdiImageProcessor.ProcessImage(msi, mso, settings); return mso; } };
		Func<Stream> mag = () => { using (var msi = new MemoryStream(inImage)) { var mso = new MemoryStream(16384); MagicImageProcessor.ProcessImage(msi, mso, settings); return mso; } };
		Func<Stream> wic = () => { using (var msi = new MemoryStream(inImage)) { var mso = new MemoryStream(16384); WicImageProcessor.ProcessImage(msi, mso, settings); return mso; } };
		Func<Stream> irf = () => { using (var msi = new MemoryStream(inImage)) { var mso = new MemoryStream(16384); ImageBuilder.Current.Build(msi, mso, irs); return mso; } };

		try
		{
			var irfimg = Task.Run(irf).Result;
			var gdiimg = Task.Run(gdi).Result;
			var wicimg = Task.Run(wic).Result;
			var magimg = Task.Run(mag).Result;

			File.WriteAllBytes($"imgirf.{settings.SaveFormat.ToString().ToLower()}", ((MemoryStream)irfimg).ToArray());
			File.WriteAllBytes($"imggdi.{settings.SaveFormat.ToString().ToLower()}", ((MemoryStream)gdiimg).ToArray());
			File.WriteAllBytes($"imgmag.{settings.SaveFormat.ToString().ToLower()}", ((MemoryStream)magimg).ToArray());
			File.WriteAllBytes($"imgwic.{settings.SaveFormat.ToString().ToLower()}", ((MemoryStream)wicimg).ToArray());

			irfimg.Position = gdiimg.Position = wicimg.Position = magimg.Position = 0;

			img1.Source = BitmapFrame.Create(irfimg, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
			img2.Source = BitmapFrame.Create(gdiimg, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
			img3.Source = BitmapFrame.Create(wicimg, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
			img4.Source = BitmapFrame.Create(magimg, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

			if (breakup)
			{
				GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
				GC.Collect(2, GCCollectionMode.Forced, true);
				Thread.Sleep(1000);
			}

			runTest(irf, $"FastScaling Auto {(speed == -2 ? null : "(speed=" + speed + ")")}", lbl1, breakup);
			runTest(gdi, $"GDI+ HighQualityBicubic {(settings.HybridMode == HybridScaleMode.Off ? null : " Hybrid (" + settings.HybridMode + ")")}", lbl2, breakup);
			runTest(wic, $"WIC Fant", lbl3, breakup);
			runTest(mag, $"MagicScaler {(settings.HybridMode == HybridScaleMode.Off ? null : " Hybrid (" + settings.HybridMode + ")")}", lbl4, breakup);
		}
		catch (Exception ex)
		{
			MessageBox.Show(ex.ToString());
		}
	}
}
