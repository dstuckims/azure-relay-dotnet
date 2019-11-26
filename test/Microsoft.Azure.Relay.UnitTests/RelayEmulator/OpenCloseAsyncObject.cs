namespace Microsoft.Azure.Relay.UnitTests
{
    using System;
    using System.Threading.Tasks;

    abstract class OpenCloseAsyncObject
    {
        bool openCalled;
        bool closeCalled;
        bool raisedClosing;

        public OpenCloseAsyncObject(TrackingContext trackingContext)
        {
            this.TrackingContext = trackingContext ?? TrackingContext.Create(Guid.NewGuid().ToString(), null);
            this.ThisLock = new object();
        }

        public event EventHandler Closing;

        public Exception TerminalException { get; private set; }

        public TrackingContext TrackingContext { get; }

        protected object ThisLock { get; }

        public async Task OpenAsync()
        {
            try
            {
                lock (this.ThisLock)
                {
                    if (this.openCalled)
                    {
                        throw Log.ThrowingException(new InvalidOperationException($"Open was called twice. {this.TrackingContext}"), this);
                    }
                    else if (this.closeCalled)
                    {
                        throw Log.ThrowingException(new InvalidOperationException($"Entity is already closed or aborted. {this.TrackingContext}"), this);
                    }

                    this.openCalled = true;
                }

                this.OnOpening();
                await this.OnOpenAsync();
                this.OnOpened();
            }
            catch (Exception e)
            {
                var closeTask = Task.Run(() => this.CloseAsync(e));
                throw;
            }
        }

        public async Task CloseAsync(Exception exception)
        {
            lock (this.ThisLock)
            {
                if (this.closeCalled)
                {
                    return;
                }

                this.closeCalled = true;
                this.TerminalException = exception;
            }

            try
            {
                this.OnClosing();
                await this.OnCloseAsync();
            }
            catch (Exception e)
            {
                Log.HandledException(e, this);
            }
            finally
            {
                this.OnClosed();
            }
        }
        
        public override string ToString()
        {
            return $"{this.GetType().Name}({this.TrackingContext})";
        }

        // OnOpening -> OnOpenAsync -> OnOpened
        protected virtual void OnOpening()
        {
        }

        protected abstract Task OnOpenAsync();

        protected virtual void OnOpened()
        {
        }

        // OnClosing -> OnCloseAsync -> OnClosed
        protected virtual void OnClosing()
        {
            lock (this.ThisLock)
            {
                if (this.raisedClosing)
                {
                    return;
                }

                this.raisedClosing = true;
            }

            this.Closing?.Invoke(this, EventArgs.Empty);
        }

        protected abstract Task OnCloseAsync();

        protected virtual void OnClosed()
        {
        }
    }
}
