﻿using System;
using LegendaryExplorerCore.Helpers;
using Serilog;

namespace ME3TweaksCore.Diagnostics
{
    /// <summary>
    /// Logger for ME3Tweaks software. Pass an ILogger into this class to set the logger to sync with your external software.
    /// </summary>
    public class MLog
    {
        public static void SetLogger(ILogger logger)
        {
            Log.Logger = logger;
        }

        /// <summary>
        /// Logging prefix for ME3TWEAKSCORE logs.
        /// </summary>
        private const string LoggingPrefix = @"[ME3TWEAKSCORE] ";

        /// <summary>
        /// Logs a string to the log. You can specify a boolean for log checking (for making calls easier)
        /// </summary>
        /// <param name="message"></param>
        /// <param name="prefix"></param>
        /// <param name="shouldLog"></param>
        public static void Information(string message, bool shouldLog = true)
        {
            if (shouldLog)
            {
                Log.Information($@"{LoggingPrefix}{message}");
            }
        }

        /// <summary>
        /// Logs a string to the log. You can specify a boolean for log checking (for making calls easier)
        /// </summary>
        /// <param name="message"></param>
        /// <param name="prefix"></param>
        /// <param name="shouldLog"></param>
        internal static void Warning(string message, bool shouldLog = true)
        {
            if (shouldLog)
            {
                Log.Warning($@"{LoggingPrefix}{message}");
            }
        }

        /// <summary>
        /// Logs a string to the log. You can specify a boolean for log checking (for making calls easier)
        /// </summary>
        /// <param name="message"></param>
        /// <param name="prefix"></param>
        /// <param name="shouldLog"></param>
        public static void Error(string message, string prefix = null, bool shouldLog = true)
        {
            if (shouldLog)
            {
                Log.Error($@"{prefix ?? LoggingPrefix}{message}");
            }
        }

        /// <summary>
        /// Logs a string to the log. You can specify a boolean for log checking (for making calls easier)
        /// </summary>
        /// <param name="message"></param>
        /// <param name="prefix"></param>
        /// <param name="shouldLog"></param>
        public static void Error(Exception ex, bool shouldLog = true)
        {
            if (shouldLog)
            {
                Log.Error($@"{LoggingPrefix}{ex.FlattenException()}");
            }
        }

        /// <summary>
        /// Logs a string to the log. You can specify a boolean for log checking (for making calls easier)
        /// </summary>
        /// <param name="message"></param>
        /// <param name="prefix"></param>
        /// <param name="shouldLog"></param>
        internal static void Fatal(string message, string prefix = null, bool shouldLog = true)
        {
            if (shouldLog)
            {
                Log.Fatal($@"{prefix ?? LoggingPrefix}{message}");
            }
        }

        /// <summary>
        /// Calls Log.CloseAndFlush(). This is here just so everything routes through MLog.
        /// </summary>
        public static void CloseAndFlush()
        {
            Log.CloseAndFlush();

        }

        public static void Exception(Exception exception, string preMessage, bool fatal = false)
        {
            Log.Error($@"{LoggingPrefix}{preMessage}");

            // Log exception
            while (exception != null)
            {
                var line1 = exception.GetType().Name + @": " + exception.Message;
                foreach (var line in line1.Split("\n")) // do not localize
                {
                    if (fatal)
                        Log.Fatal(LoggingPrefix + line);
                    else
                        Log.Error(LoggingPrefix + line);

                }

                if (exception.StackTrace != null)
                {
                    foreach (var line in exception.StackTrace.Split("\n")) // do not localize
                    {
                        if (fatal)
                            Log.Fatal(LoggingPrefix + line);
                        else
                            Log.Error(LoggingPrefix + line);
                    }
                }

                exception = exception.InnerException;
            }
        }

        public static void Debug(string message, string prefix = null, bool shouldLog = true)
        {
            if (shouldLog)
            {
                Log.Debug($@"{prefix ?? LoggingPrefix}{message}");
                System.Diagnostics.Debug.WriteLine($@"{prefix ?? LoggingPrefix}{message}");
            }
        }
    }
}
