﻿using System;
using System.Threading.Tasks;
using WampSharp.V2.Rpc;

namespace WampSharp.Samples.Callee
{
    public interface ILongOpService
    {
        [WampProcedure("com.myapp.longop")]
        [WampProgressiveResultProcedure]
        Task<int> LongOp(int n, IProgress<int> progress);
    }

    public class LongOpService : ILongOpService
    {
        public async Task<int> LongOp(int n, IProgress<int> progress)
        {
            for (int i = 0; i < n; i++)
            {
                progress.Report(i);
                await Task.Delay(100);
            }

            return n;
        }
    }
}