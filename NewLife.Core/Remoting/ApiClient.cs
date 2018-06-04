﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NewLife.Collections;
using NewLife.Data;
using NewLife.Log;
using NewLife.Messaging;
using NewLife.Net;
using NewLife.Net.Handlers;
using NewLife.Threading;

namespace NewLife.Remoting
{
    /// <summary>应用接口客户端</summary>
    public class ApiClient : ApiHost, IApiSession
    {
        #region 属性
        /// <summary>是否已打开</summary>
        public Boolean Active { get; set; }

        /// <summary>服务端地址集合。负载均衡</summary>
        public List<String> Servers { get; set; } = new List<String>();

        ///// <summary>通信客户端</summary>
        //public ISocketClient Client { get; set; }

        /// <summary>主机</summary>
        IApiHost IApiSession.Host => this;

        /// <summary>最后活跃时间</summary>
        public DateTime LastActive { get; set; }

        /// <summary>所有服务器所有会话，包含自己</summary>
        IApiSession[] IApiSession.AllSessions => new IApiSession[] { this };

        /// <summary>调用超时时间。默认30_000ms</summary>
        public Int32 Timeout { get; set; } = 30_000;

        /// <summary>发送数据包统计信息</summary>
        public ICounter StatSend { get; set; }

        /// <summary>接收数据包统计信息</summary>
        public ICounter StatReceive { get; set; }
        #endregion

        #region 构造
        /// <summary>实例化应用接口客户端</summary>
        public ApiClient()
        {
            var type = GetType();
            Name = type.GetDisplayName() ?? type.Name.TrimEnd("Client");

            Register(new ApiController { Host = this }, null);
        }

        /// <summary>实例化应用接口客户端</summary>
        public ApiClient(String uri) : this()
        {
            if (!uri.IsNullOrEmpty()) Servers.AddRange(uri.Split(","));
        }

        /// <summary>销毁</summary>
        /// <param name="disposing"></param>
        protected override void OnDispose(Boolean disposing)
        {
            base.OnDispose(disposing);

            _Timer.TryDispose();

            Close(Name + (disposing ? "Dispose" : "GC"));
        }
        #endregion

        #region 打开关闭
        /// <summary>打开客户端</summary>
        public virtual Boolean Open()
        {
            if (Active) return true;

            var ss = Servers;
            if (ss == null || ss.Count == 0) throw new ArgumentNullException(nameof(Servers), "未指定服务端地址");

            if (Pool == null) Pool = new MyPool { Host = this };

            if (Encoder == null) Encoder = new JsonEncoder();
            //if (Encoder == null) Encoder = new BinaryEncoder();
            if (Handler == null) Handler = new ApiHandler { Host = this };
            if (StatInvoke == null) StatInvoke = new PerfCounter();
            if (StatProcess == null) StatProcess = new PerfCounter();
            if (StatSend == null) StatSend = new PerfCounter();
            if (StatReceive == null) StatReceive = new PerfCounter();

            Encoder.Log = EncoderLog;

            using (var pi = Pool.AcquireItem())
            {
                var ct = pi.Value;

                //ct.Log = Log;

                // 打开网络连接
                if (!ct.Open()) return false;
            }

            ShowService();

            var ms = StatPeriod * 1000;
            if (ms > 0) _Timer = new TimerX(DoWork, null, ms, ms) { Async = true };

            return Active = true;
        }

        /// <summary>关闭</summary>
        /// <param name="reason">关闭原因。便于日志分析</param>
        /// <returns>是否成功</returns>
        public virtual void Close(String reason)
        {
            if (!Active) return;

            //var ct = Client;
            //if (ct != null) ct.Close(reason ?? (GetType().Name + "Close"));
            Pool.TryDispose();
            Pool = null;

            Active = false;
        }

        ///// <summary>设置远程地址</summary>
        ///// <param name="uri"></param>
        ///// <returns></returns>
        //public Boolean SetRemote(String uri)
        //{
        //    var nu = new NetUri(uri);

        //    WriteLog("SetRemote {0}", nu);

        //    var ct = Client = nu.CreateRemote();
        //    ct.Log = Log;
        //    ct.Add(new StandardCodec { Timeout = Timeout, UserPacket = false });

        //    return true;
        //}

        /// <summary>查找Api动作</summary>
        /// <param name="action"></param>
        /// <returns></returns>
        public virtual ApiAction FindAction(String action) => Manager.Find(action);

        /// <summary>创建控制器实例</summary>
        /// <param name="api"></param>
        /// <returns></returns>
        public virtual Object CreateController(ApiAction api) => this.CreateController(this, api);
        #endregion

        #region 远程调用
        /// <summary>异步调用，等待返回结果</summary>
        /// <param name="resultType">返回类型</param>
        /// <param name="action">服务操作</param>
        /// <param name="args">参数</param>
        /// <param name="flag">标识</param>
        /// <returns></returns>
        public virtual async Task<Object> InvokeAsync(Type resultType, String action, Object args = null, Byte flag = 0)
        {
            Open();

            var act = action;

            try
            {
                return await ApiHostHelper.InvokeAsync(this, this, resultType, act, args, flag);
            }
            catch (ApiException ex)
            {
                // 重新登录后再次调用
                if (ex.Code == 401)
                {
                    return await ApiHostHelper.InvokeAsync(this, this, resultType, act, args, flag);
                }

                throw;
            }
            // 截断任务取消异常，避免过长
            catch (TaskCanceledException ex)
            {
                throw new TaskCanceledException($"[{action}]超时取消", ex);
            }
        }

        /// <summary>异步调用，等待返回结果</summary>
        /// <typeparam name="TResult"></typeparam>
        /// <param name="action">服务操作</param>
        /// <param name="args">参数</param>
        /// <param name="flag">标识</param>
        /// <returns></returns>
        public virtual async Task<TResult> InvokeAsync<TResult>(String action, Object args = null, Byte flag = 0)
        {
            var rs = await InvokeAsync(typeof(TResult), action, args, flag);
            return (TResult)rs;
        }

        /// <summary>同步调用，不等待返回</summary>
        /// <param name="action">服务操作</param>
        /// <param name="args">参数</param>
        /// <param name="flag">标识</param>
        /// <returns></returns>
        public virtual Boolean Invoke(String action, Object args = null, Byte flag = 0)
        {
            if (!Open()) return false;

            var act = action;

            return ApiHostHelper.Invoke(this, this, act, args, flag);
        }

        /// <summary>创建消息</summary>
        /// <param name="pk"></param>
        /// <returns></returns>
        IMessage IApiSession.CreateMessage(Packet pk) => new DefaultMessage { Payload = pk };

        async Task<IMessage> IApiSession.SendAsync(IMessage msg)
        {
            Exception last = null;
            var count = Servers.Count;
            for (var i = 0; i < count; i++)
            {
                var client = Pool.Acquire();
                try
                {
                    return (await client.SendMessageAsync(msg)) as IMessage;
                }
                catch (ApiException) { throw; }
                catch (Exception ex)
                {
                    last = ex;
                    client.TryDispose();
                }
                finally
                {
                    Pool.Release(client);
                }
            }

            throw last;
        }

        Boolean IApiSession.Send(IMessage msg)
        {
            Exception last = null;
            var count = Servers.Count;
            for (var i = 0; i < count; i++)
            {
                var client = Pool.Acquire();
                try
                {
                    return client.SendMessage(msg);
                }
                catch (Exception ex) { last = ex; }
                finally
                {
                    Pool.Release(client);
                }
            }

            throw last;
        }
        #endregion

        #region 连接池
        /// <summary>连接池</summary>
        public Pool<ISocketClient> Pool { get; private set; }

        class MyPool : Pool<ISocketClient>
        {
            public ApiClient Host { get; set; }

            public MyPool()
            {
                // 最小值为0，连接池不再使用栈，只使用队列
                Min = 0;
                Max = 100000;
            }

            protected override ISocketClient Create() => Host.OnCreate();
        }

        /// <summary>Round-Robin 负载均衡</summary>
        private Int32 _index = -1;

        /// <summary>为连接池创建连接</summary>
        /// <returns></returns>
        protected virtual ISocketClient OnCreate()
        {
            // 遍历所有服务，找到可用服务端
            var ss = Servers.ToArray();
            if (ss.Length == 0) throw new InvalidOperationException("没有设置服务端地址Servers");

            var idx = Interlocked.Increment(ref _index);
            Exception last = null;
            for (var i = 0; i < ss.Length; i++)
            {
                // Round-Robin 负载均衡
                var k = (idx + i) % ss.Length;
                var svr = ss[k];
                try
                {
                    var client = new NetUri(svr).CreateRemote();
                    client.Timeout = Timeout;
                    if (Log != null) client.Log = Log;
                    client.StatSend = StatSend;
                    client.StatReceive = StatReceive;

                    client.Add(new StandardCodec { Timeout = Timeout, UserPacket = false });
                    client.Open();

                    return client;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            throw last;
        }
        #endregion

        #region 统计
        private TimerX _Timer;
        private String _Last;

        /// <summary>显示统计信息的周期。默认600秒，0表示不显示统计信息</summary>
        public Int32 StatPeriod { get; set; } = 600;

        private void DoWork(Object state)
        {
            var msg = this.GetStat();
            if (msg == _Last) return;
            _Last = msg;

            WriteLog(msg);
        }
        #endregion
    }
}