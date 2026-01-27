using System;
using System.Globalization;
using System.Windows.Data;
using EnshroudedPlanner;

namespace EnshroudedPlanner.UI
{
    /// <summary>
    /// XAML Converter: Piece -> ImageSource thumbnail.
    /// </summary>
    public sealed class PieceThumbnailConverter : IValueConverter
    {
        public int PixelSize { get; set; } = 64;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Piece p)
                return PieceThumbnailService.GetOrCreate(p, PixelSize);

            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}