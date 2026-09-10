using System;

namespace InternetRadio
{
    /// <summary>
    /// Tags reported by a container header (for Ogg Vorbis these come from the Vorbis comment).
    /// Any field may be null: streams usually carry only a few. For Icecast Ogg streams the
    /// comment often holds nothing but <c>encoder=</c>, so station name and genre are better
    /// taken from the response headers (<c>icy-name</c>, <c>icy-genre</c>).
    /// </summary>
    public readonly struct AudioTags
    {
        public readonly string Title;
        public readonly string Artist;
        public readonly string Album;
        public readonly string Genre;

        public AudioTags(string title, string artist, string album, string genre)
        {
            Title = title;
            Artist = artist;
            Album = album;
            Genre = genre;
        }

        public bool IsEmpty =>
            string.IsNullOrEmpty(Title) && string.IsNullOrEmpty(Artist) &&
            string.IsNullOrEmpty(Album) && string.IsNullOrEmpty(Genre);
    }
}
