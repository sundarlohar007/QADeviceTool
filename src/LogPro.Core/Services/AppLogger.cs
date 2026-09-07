using NLog;
using NLog.Config;
using NLog.Targets;
using System;
using System.IO;

namespace LogPro.Services;

public static class AppLogger
{
    private static readonly Logger _logger;

    static AppLogger()
    {
        var config = new LoggingConfiguration();

        var logDirectory = Path.Combine(Helpers.PathHelper.GetAppDataDirectory(), "logs");

        var canPersistLogs = false;
        try
        {
            Directory.CreateDirectory(logDirectory);
            canPersistLogs = Helpers.PathHelper.RestrictDirectoryAccess(logDirectory);
        }
        catch { /* Secure mode fails closed for persistent logs. */ }

        var logfile = new FileTarget("logfile")
        {
            FileName = Path.Combine(logDirectory, "app-log-${shortdate}.txt"),
            // Exception messages and stacks can contain URLs, tokens, serials, or local
            // paths. Details belong in the redacted issue pipeline, not persistent logs.
            Layout = "${longdate}|${level:uppercase=true}|${logger}|${message} ${exception:format=Type}",
            ArchiveAboveSize = 5242880, // 5MB
            MaxArchiveFiles = 5,
            KeepFileOpen = true
        };

        var logconsole = new ConsoleTarget("logconsole")
        {
            Layout = "${longdate}|${level:uppercase=true}|${logger}|${message} ${exception:format=Type}"
        };

        config.AddRule(LogLevel.Debug, LogLevel.Fatal, logconsole);
        if (canPersistLogs)
            config.AddRule(LogLevel.Info, LogLevel.Fatal, logfile);

        LogManager.Configuration = config;
        _logger = LogManager.GetCurrentClassLogger();
    }

    public static Logger Log => _logger;

    public static Logger GetLogger<T>()
    {
        return LogManager.GetLogger(typeof(T).FullName ?? typeof(T).Name);
    }
}
