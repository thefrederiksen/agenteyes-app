using System;
Console.WriteLine($"runtime={Environment.Version} HOME={Environment.GetEnvironmentVariable("HOME")}");
foreach (var f in new[]{Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData})
  Console.WriteLine($"{f}={Environment.GetFolderPath(f)}");
