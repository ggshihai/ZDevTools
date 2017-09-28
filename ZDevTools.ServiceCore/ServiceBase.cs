﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ServiceStack.Redis;
using System.Diagnostics;
using Newtonsoft.Json;
using System.IO;

namespace ZDevTools.ServiceCore
{
    /// <summary>
    /// 服务基础抽象类
    /// </summary>
    public abstract class ServiceBase : IServiceBase
    {
        public ServiceBase()
        {
            this.ServiceName = this.GetType().Name; //设置服务名称
        }

        static readonly log4net.ILog log = log4net.LogManager.GetLogger(typeof(ServiceBase));
        void logInfo(string message) => log.Info($"【{DisplayName}】{message}");
        void logWarn(string message, Exception exception) => log.Error($"【{DisplayName}】{message}", exception);

        public const string ServiceReportsFolder = "reports";
        public static RedisManagerPool RedisManagerPool { get; } = string.IsNullOrEmpty(Properties.Settings.Default.RedisServer) ? null : new RedisManagerPool(Properties.Settings.Default.RedisServer);

        public abstract string DisplayName { get; }

        /// <summary>
        /// 服务內部名称（其实就是类名）
        /// </summary>
        public string ServiceName { get; }

        /// <summary>
        /// 提供一个标准的时间格式化方法
        /// </summary>
        /// <param name="dateTime">要被格式化的日期时间实例</param>
        /// <returns></returns>
        public static string FormatDateTime(DateTime dateTime) => $"{dateTime:yyyy-MM-dd HH:mm:ss}";

        protected abstract log4net.ILog Log { get; }

        /// <summary>
        /// 记录提示性信息
        /// </summary>
        /// <param name="message"></param>
        public void LogInfo(string message)
        {
            Log.Info($"【{DisplayName}】{message}");
        }

        /// <summary>
        /// 记录调试信息
        /// </summary>
        /// <param name="message"></param>
        public void LogDebug(string message)
        {
            Log.Debug($"【{DisplayName}】{message}");
        }

        /// <summary>
        /// 记录调试信息
        /// </summary>
        /// <param name="message"></param>
        public void LogDebug(string message, Exception exception)
        {
            Log.Debug($"【{DisplayName}】{message}", exception);
        }

        /// <summary>
        /// 记录一般性警告错误
        /// </summary>
        /// <param name="message"></param>
        public void LogWarn(string message)
        {
            Log.Warn($"【{DisplayName}】{message}");
        }

        /// <summary>
        /// 记录一般性警告错误
        /// </summary>
        /// <param name="message"></param>
        /// <param name="exception"></param>
        public void LogWarn(string message, Exception exception)
        {
            Log.Warn($"【{DisplayName}】{message}", exception);
        }

        /// <summary>
        /// 扔出较为严重的错误，该错误发生后任务彻底终止
        /// </summary>
        /// <param name="message">错误消息</param>
        [DebuggerNonUserCode]
        public void ThrowError(string message)
        {
            throw new ServiceErrorException(message);
        }

        /// <summary>
        /// 扔出较为严重的错误，该错误发生后任务彻底终止
        /// </summary>
        /// <param name="message">错误消息</param>
        /// <param name="innerException">內部异常</param>
        [DebuggerNonUserCode]
        public void ThrowError(string message, Exception innerException)
        {
            throw new ServiceErrorException(message, innerException);
        }

        #region 已导出
        /// <summary>
        /// 保存Hash对象
        /// </summary>
        public void SaveHash(string hashId, Dictionary<string, string> dic)
        {
            if (RedisManagerPool == null)
                return;

            using (var client = RedisManagerPool.GetClient())
            {
                using (var trans = client.CreateTransaction())
                {
                    trans.QueueCommand(rc => rc.Remove(hashId));
                    trans.QueueCommand(rc => rc.SetRangeInHash(hashId, dic));
                    trans.Commit();
                }
            }

            LogInfo($"生成条目{dic.Count}条，已保存到hashid为{hashId}的哈希集合中");
        }

        /// <summary>
        /// 保存单值字符串
        /// </summary>
        public void SaveValue(string key, string value)
        {
            if (RedisManagerPool == null)
                return;

            using (var client = RedisManagerPool.GetClient())
            {
                client.SetValue(key, value);
            }

            LogInfo($"生成条目{key}");
        }

        void reportStatus(ServiceReport serviceReport)
        {
            try
            {
                serviceReport.ServiceName = DisplayName;
                serviceReport.UpdateTime = DateTime.Now;

                writeReport(serviceReport);

                if (RedisManagerPool != null)
                    using (var client = RedisManagerPool.GetClient())
                    {
                        client.SetEntryInHash(RedisKeys.ServiceReports, ServiceName, JsonConvert.SerializeObject(serviceReport));
                        client.PublishMessage(RedisKeys.ServiceReports, ServiceName);
                    }
            }
            catch (Exception ex) { LogWarn("状态汇报出错，错误：" + ex.Message, ex); }
        }

        FileStream _reportStream;
        StreamWriter _reportStreamWriter;
        static readonly object ReportLocker = new object();
        /// <summary>
        /// 写入报告（该方法允许多线程调用）
        /// </summary>
        /// <param name="serviceReport">服务报告</param>
        void writeReport(ServiceReport serviceReport)
        {
            lock (ReportLocker)
            {
                if (_reportStream == null)
                {
                    string saveFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ServiceReportsFolder);
                    if (!Directory.Exists(saveFolder))
                        Directory.CreateDirectory(saveFolder);

                    string reportFullName = Path.Combine(saveFolder, ServiceName + ".log");

                    bool fileExists = File.Exists(reportFullName);

                    _reportStream = File.Open(reportFullName, FileMode.Append, FileAccess.Write, FileShare.Read);
                    _reportStreamWriter = new StreamWriter(_reportStream);

                    if (!fileExists) //write log header
                        _reportStreamWriter.WriteLine("时间\t\t\t错误\t消息\t\t\t消息组");
                }

                _reportStreamWriter.WriteLine($"{FormatDateTime(serviceReport.UpdateTime)}\t{(serviceReport.HasError ? "[有]" : "[无]")}\t{serviceReport.Message}\t{(serviceReport.MessageArray == null ? null : string.Join("、", serviceReport.MessageArray))}");

                _reportStreamWriter.Flush();
            }
        }

        /// <summary>
        /// 报告服务状态
        /// </summary>
        /// <param name="message"></param>
        public void ReportStatus(string message)
        {
            ServiceReport report = new ServiceReport();

            report.Message = message;

            reportStatus(report);
        }

        /// <summary>
        /// 报告服务执行状态及额外信息
        /// </summary>
        /// <param name="executionExtraInfo"></param>
        public void ReportStatus(ExecutionExtraInfo executionExtraInfo)
        {
            ServiceReport report = new ServiceReport();

            if (executionExtraInfo != null)
            {
                report.Message = executionExtraInfo.WarnMessage;
                report.MessageArray = executionExtraInfo.WarnMessageArray;
            }

            reportStatus(report);
        }

        /// <summary>
        /// 报告服务错误
        /// </summary>
        /// <param name="message"></param>
        public void ReportError(string message)
        {
            ServiceReport report = new ServiceReport();

            report.HasError = true;
            report.Message = message;

            reportStatus(report);
        }

        /// <summary>
        /// 报告服务错误
        /// </summary>
        /// <param name="message"></param>
        /// <param name="exception"></param>
        public void ReportError(string message, Exception exception)
        {
            ServiceReport report = new ServiceReport();

            report.HasError = true;
            report.Message = message;
            report.MessageArray = new List<string>() { exception.Message };

            reportStatus(report);
        }
        #endregion
    }

}
