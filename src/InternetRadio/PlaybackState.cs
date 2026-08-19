namespace InternetRadio
{
    /// <summary>Current lifecycle state of the player.</summary>
    public enum PlaybackState
    {
        /// <summary>Not started (initial state) or fully stopped.</summary>
        Idle,

        /// <summary>Resolving the address and connecting to the station.</summary>
        Connecting,

        /// <summary>Connected; accumulating a playback buffer before decoding begins.</summary>
        Buffering,

        /// <summary>Decoding and delivering PCM samples.</summary>
        Playing,

        /// <summary>Stopped after an unrecoverable error (see the Error event).</summary>
        Faulted
    }
}
