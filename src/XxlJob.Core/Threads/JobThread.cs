﻿using com.xxl.job.core.biz.model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XxlJob.Core.Threads
{
    public class JobThread
    {
        private readonly JobExecutorOption _executorConfig;
        private readonly ConcurrentQueue<TriggerParam> _triggerQueue;
        // avoid repeat trigger for the same TRIGGER_LOG_ID
        private readonly ConcurrentDictionary<int, byte> _triggerLogIdSet;
        private readonly AutoResetEvent _queueHasDataEvent;
        private readonly ILogger _logger;
        private readonly JobHandlerFactory _jobHandlerFactory;

        private Thread _thread;
        private bool _running = false;
        private volatile bool _toStop = false;
        private string _stopReason;

        public event EventHandler<HandleCallbackParam> OnCallback;

        public bool Stopped { get { return _toStop; } }

        public JobThread(IOptions<JobExecutorOption> executorConfig, ILoggerFactory loggerFactory, JobHandlerFactory jobHandlerFactory)
        {
            _executorConfig = executorConfig.Value;
            _triggerQueue = new ConcurrentQueue<TriggerParam>();
            _triggerLogIdSet = new ConcurrentDictionary<int, byte>();
            _queueHasDataEvent = new AutoResetEvent(false);
            _logger = loggerFactory.CreateLogger<JobThread>();
            _jobHandlerFactory = jobHandlerFactory;
        }

        public ReturnT PushTriggerQueue(TriggerParam triggerParam)
        {
            // avoid repeat
            if (_triggerLogIdSet.ContainsKey(triggerParam.logId))
            {
                _logger.LogInformation("repeate trigger job, logId:{logId}", triggerParam.logId);
                return ReturnT.CreateFailedResult("repeate trigger job, logId:" + triggerParam.logId);
            }

            _logger.LogInformation("repeate trigger job, logId:{logId}", triggerParam.logId);

            _triggerLogIdSet[triggerParam.jobId] = 0;
            _triggerQueue.Enqueue(triggerParam);
            _queueHasDataEvent.Set();
            return ReturnT.SUCCESS;
        }

        public void Start()
        {
            _thread = new Thread(Run);
            _thread.Start();
        }

        public void ToStop(string stopReason)
        {
            /**
             * Thread.interrupt只支持终止线程的阻塞状态(wait、join、sleep)，
             * 在阻塞出抛出InterruptedException异常,但是并不会终止运行的线程本身；
             * 所以需要注意，此处彻底销毁本线程，需要通过共享变量方式；
             */
            _toStop = true;
            _stopReason = stopReason;
        }

        public void Interrupt(string stopReason)
        {
            ToStop(stopReason);
            _thread?.Interrupt();
        }

        public bool IsRunningOrHasQueue()
        {
            return _running || _triggerQueue.Count > 0;
        }



        private void Run()
        {
            TriggerParam triggerParam = null;
            while (!_toStop)
            {
                _running = false;
                triggerParam = null;
                ReturnT executeResult = null;
                try
                {
                    if (_triggerQueue.TryDequeue(out triggerParam))
                    {
                        _running = true;
                        byte temp;
                        _triggerLogIdSet.TryRemove(triggerParam.logId, out temp);
                        JobLogger.SetLogFileName(triggerParam.logDateTim, triggerParam.logId);
                        var executionContext = new JobExecutionContext()
                        {
                            BroadcastIndex = triggerParam.broadcastIndex,
                            BroadcastTotal = triggerParam.broadcastTotal,
                            ExecutorParams = triggerParam.executorParams
                        };
                        // execute
                        JobLogger.Log("<br>----------- xxl-job job execute start -----------<br>----------- Param:" + triggerParam.executorParams);

                        var handler = _jobHandlerFactory.GetJobHandler(triggerParam.executorHandler);
                        executeResult = handler.Execute(executionContext);
                        JobLogger.Log("<br>----------- xxl-job job execute end(finish) -----------<br>----------- ReturnT:" + executeResult);
                    }
                    else
                    {
                        if (!_queueHasDataEvent.WaitOne(Constants.JobThreadWaitTime))
                        {
                            ToStop("excutor idel times over limit.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (triggerParam != null)
                    {
                        if (_toStop)
                        {
                            JobLogger.Log("<br>----------- JobThread toStop, stopReason:" + _stopReason);
                        }
                        var errorMsg = ex.ToString();
                        executeResult = ReturnT.CreateFailedResult(errorMsg);
                        JobLogger.Log("<br>----------- JobThread Exception:" + errorMsg + "<br>----------- xxl-job job execute end(error) -----------");
                    }
                    else
                    {
                        _logger.LogError(ex, "JobThread exception.");
                    }
                }
                finally
                {
                    if (triggerParam != null)
                    {
                        OnCallback?.Invoke(this, new HandleCallbackParam(triggerParam.logId, triggerParam.logDateTim, executeResult ?? ReturnT.FAIL));
                    }
                }
            }

            // callback trigger request in queue
            while (_triggerQueue.TryDequeue(out triggerParam))
            {
                var stopResult = ReturnT.CreateFailedResult(_stopReason + " [job not executed, in the job queue, killed.]");
                OnCallback?.Invoke(this, new HandleCallbackParam(triggerParam.logId, triggerParam.logDateTim, stopResult));
            }
        }
    }
}
