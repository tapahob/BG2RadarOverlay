using NLog;
using NLog.Targets;
using NLog.Targets.Wrappers;
using System;
using System.Collections.Concurrent;
using System.IO;

namespace BGOverlay
{
    public static class Logger
    {
        private static NLog.Logger logger;
        private static readonly ConcurrentDictionary<string, string> errorCache = new ConcurrentDictionary<string, string>();

        public static void Init()
        {
            if (logger != null) return;
            
            var config = new NLog.Config.LoggingConfiguration();
            
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BG2RadarOverlay");
            var logfile = new FileTarget("logfile") {
                FileName = Path.Combine(folder, "radar.log"), ArchiveOldFileOnStartup = true,
                ArchiveAboveSize = 8 * 1024 * 1024, MaxArchiveFiles = 2,
                Layout = "${longdate} [${level:uppercase=true}] ${message} ${exception:format=tostring}"
            };
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, new AsyncTargetWrapper(logfile));

            LogManager.Configuration = config;
            logger = LogManager.GetCurrentClassLogger();
            logger.Info($"Radar {Configuration.Version} started; OS={Environment.OSVersion}, 64-bit={Environment.Is64BitProcess}");
        }

        public static void Info(string msg, params object[] args)
        {            
            logger.Info(msg, args);
        }

        public static void Debug(string msg, params object[] args)
        {
            logger.Debug(msg, args);        
        }

        public static void Error(string msg, Exception ex = null)
        {
            if (ex == null) logger.Error(msg);
            else if (errorCache.TryAdd(ex.Message + ex.StackTrace, msg))
            {
                logger.Error(ex, msg);
            }                
        }

        public static void Fatal(string msg, Exception ex = null)
        {
            if (ex != null && !errorCache.ContainsKey(ex.Message + ex.StackTrace))
            {
                errorCache[ex.Message + ex.StackTrace] = msg;
                logger.Fatal(ex, msg);
            }
        }

        public static void flush()
        {
            LogManager.Flush();
        }
    }
}
