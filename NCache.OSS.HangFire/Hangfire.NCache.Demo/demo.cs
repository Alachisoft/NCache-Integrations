using System;
using Hangfire.Logging;
using Microsoft.Extensions.Logging;

namespace YourApp.Logging
{
    public class MicrosoftLoggingProvider : ILogProvider
    {
        private readonly ILoggerFactory _factory;
        public MicrosoftLoggingProvider(ILoggerFactory factory) => _factory = factory;
        public ILog GetLogger(string name) => new MicrosoftLoggingLogger(_factory.CreateLogger(name));
    }

    internal class MicrosoftLoggingLogger : ILog
    {
        private readonly Microsoft.Extensions.Logging.ILogger _logger;
        public MicrosoftLoggingLogger(Microsoft.Extensions.Logging.ILogger logger) => _logger = logger;

        public bool Log(Hangfire.Logging.LogLevel logLevel, Func<string> messageFunc, Exception exception = null)
        {
            var level = ToMsLevel(logLevel);

            // Hangfire calls this with messageFunc == null purely to ask "is this level
            // enabled?" before it bothers building a more expensive message elsewhere —
            // has to be handled or you risk a NullReferenceException on that check.
            if (messageFunc == null) return _logger.IsEnabled(level);
            if (!_logger.IsEnabled(level)) return false;

            _logger.Log(level, exception, messageFunc());
            return true;
        }

        private static Microsoft.Extensions.Logging.LogLevel ToMsLevel(Hangfire.Logging.LogLevel level)
        {
            switch (level)
            {
                case Hangfire.Logging.LogLevel.Trace: return Microsoft.Extensions.Logging.LogLevel.Trace;
                case Hangfire.Logging.LogLevel.Debug: return Microsoft.Extensions.Logging.LogLevel.Debug;
                case Hangfire.Logging.LogLevel.Info: return Microsoft.Extensions.Logging.LogLevel.Information;
                case Hangfire.Logging.LogLevel.Warn: return Microsoft.Extensions.Logging.LogLevel.Warning;
                case Hangfire.Logging.LogLevel.Error: return Microsoft.Extensions.Logging.LogLevel.Error;
                case Hangfire.Logging.LogLevel.Fatal: return Microsoft.Extensions.Logging.LogLevel.Critical;
                default: return Microsoft.Extensions.Logging.LogLevel.None;
            }
        }
    }
}