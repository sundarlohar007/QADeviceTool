using NLog;
using NLog.Config;
using NLog.Targets;
using System;
using System.IO;

namespace QADeviceTool.Services;

public static class AppLogger
{
    private static readonly Logger _logger;

    static AppLogger()
    {
        var config = new LoggingConfiguration();

        var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QAQCDeviceTool", "logs");
        
        // Ensure directory exists
        if (!Directory.Exists(logDirectory))
        {
            Directory.CreateDirectory(logDirectory);
        }

        var logfile = new FileTarget("logfile")
        {
            FileName = Path.Combine(logDirectory, "app-log-${shortdate}.txt"),
            Layout = "${longdate}|${level:uppercase=true}|${logger}|${message} ${exception:format=ToString}",
            ArchiveAboveSize = 5242880, // 5MB
            MaxArchiveFiles = 5,
            KeepFileOpen = true
        };

        var logconsole = new ConsoleTarget("logconsole")
        {
            Layout = "${longdate}|${level:uppercase=true}|${logger}|${message} ${exception:format=ToString}"
        };

        config.AddRule(LogLevel.Debug, LogLevel.Fatal, logconsole);
        config.AddRule(LogLevel.Info, LogLevel.Fatal, logfile);

        LogManager.Configuration = config;
        _logger = LogManager.GetCurrentClassLogger();
    }

    public static Logger Log => _logger;

    public static Logger GetLogger<T>()
    {
        return LogManager.GetLogger(typeof(T).FullName);
    }
}
