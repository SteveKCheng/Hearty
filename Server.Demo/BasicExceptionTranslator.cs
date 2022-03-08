﻿using System;
using Hearty.Common;

namespace Hearty.Server.Demo
{
    public static class BasicExceptionTranslator
    {
        private static PromiseData Translate(PromiseId? promiseId, Exception exception)
        {
            // Unwrap exceptions from worker hosts executing the job
            if (exception is RemoteWorkException remoteWorkException)
                return new PromiseExceptionalData(remoteWorkException.Payload);
            else
                return new PromiseExceptionalData(exception);
        }

        public static PromiseExceptionTranslator Instance { get; } = Translate;
    }
}