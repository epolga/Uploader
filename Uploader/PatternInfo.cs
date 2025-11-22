using System;
using System.IO;
using System.Windows;
using Size = System.Windows.Size;
using MessageBox = System.Windows.MessageBox;

namespace UploadPatterns
{
    /// <summary>
    /// Holds parsed information about a single cross-stitch pattern
    /// extracted from a PDF file (title, notes, size, colors).
    /// Also carries album / page / design IDs and Pinterest Pin ID.
    /// </summary>
    public class PatternInfo
    {
        public PatternInfo(string filePath)
        {
            ParsePdf(filePath);
        }

        private string _title = string.Empty;
        private string _notes = string.Empty;
        private string _description = string.Empty;
        private int _nColors;
        private int _width;
        private int _height;
        private int _designId = -1;
        private string _pinId = string.Empty;

        /// <summary>
        /// Album ID (set externally after reading .txt file).
        /// </summary>
        public int AlbumId { get; set; } = -1;

        /// <summary>
        /// Logical page number within album (e.g. "00001").
        /// </summary>
        public string NPage { get; set; } = string.Empty;

        /// <summary>
        /// Pinterest Pin ID created for this design (if any).
        /// </summary>
        public string PinId { get; set; } = string.Empty;

        /// <summary>
        /// Pattern title extracted from PDF.
        /// </summary>
        public string Title => _title;

        /// <summary>
        /// Raw notes block extracted from PDF (HTML-like line breaks).
        /// </summary>
        public string Notes => _notes;

        /// <summary>
        /// Short description of pattern, e.g. "100 x 120 stitches 25 colors".
        /// </summary>
        public string Description => _description;

        /// <summary>
        /// Number of colors (threads) used in the pattern.
        /// </summary>
        public int NColors => _nColors;

        /// <summary>
        /// Pattern width in stitches.
        /// </summary>
        public int Width
        {
            get => _width;
            set => _width = value;
        }

        /// <summary>
        /// Pattern height in stitches.
        /// </summary>
        public int Height
        {
            get => _height;
            set => _height = value;
        }

        /// <summary>
        /// Unique design ID in your system.
        /// </summary>
        public int DesignID
        {
            get => _designId;
            set => _designId = value;
        }


        /// <summary>
        /// ID of the pin uploaded to pinterest.
        /// </summary>
        public string PinID
        {
            get => _pinId;
            set => _pinId = value;
        }


        /// <summary>
        /// Reads PDF content as text (using a known export format) and parses title,
        /// notes, size and color count. Exceptions are reported via MessageBox.
        /// </summary>
        private void ParsePdf(string filePath)
        {
            try
            {
                string pdfContent = File.ReadAllText(filePath);

                string title = GetTitle(pdfContent) ?? string.Empty;
                string notes = GetNotes(pdfContent) ?? string.Empty;
                int nColors = GetNColors(pdfContent);

                Size size = GetSize(notes);

                _title = title;
                _notes = notes;
                _nColors = nColors;
                _width = (int)size.Width;
                _height = (int)size.Height;
                _description = $"{_width} x {_height} stitches {_nColors} colors";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{ex.Message} {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Extracts title from PDF text using fixed markers.
        /// </summary>
        private string? GetTitle(string pdfContent)
        {
            try
            {
                string before = "-0.0367  Tc 0.0967  Tw (";
                string after = ")";

                int startPos = pdfContent.IndexOf(before, StringComparison.Ordinal);
                if (startPos < 0)
                    return null;

                int titleStart = startPos + before.Length;
                if (titleStart >= pdfContent.Length)
                    return null;

                string remainder = pdfContent.Substring(titleStart);
                int endPos = remainder.IndexOf(after, StringComparison.Ordinal);
                if (endPos <= 0)
                    return null;

                string title = remainder.Substring(0, endPos - 1);
                return title.Trim();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{ex.Message} {ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// Extracts the notes block from PDF text using known markers.
        /// </summary>
        private string? GetNotes(string pdfContent)
        {
            try
            {
                string beforeNotes = "(Notes) Tj";
                int beforeNotesLength = beforeNotes.Length;
                int beforeNotesPos = pdfContent.IndexOf(beforeNotes, StringComparison.Ordinal);

                if (beforeNotesPos < 0)
                    return null;

                string notesPart = pdfContent.Substring(beforeNotesPos + beforeNotesLength);

                string[] startMarkers =
                {
                    "(aaMaterial Type:",
                    "(Material Type:",
                    "(Sewing Count:",
                    "(Design Size:",
                    "(Sewn Design Size:",
                    "(Suggested Material Size:",
                    "(Stitch Style:"
                };

                string notes = string.Empty;
                string endMarker = ")";

                foreach (var marker in startMarkers)
                {
                    int startPos = notesPart.IndexOf(marker, StringComparison.Ordinal);
                    if (startPos < 0)
                        continue;

                    // Move one char forward to skip leading "("
                    startPos++;
                    int endPos = notesPart.IndexOf(endMarker, startPos, StringComparison.Ordinal);
                    if (endPos <= startPos)
                        continue;

                    int len = endPos - startPos;
                    notes += notesPart.Substring(startPos, len);
                    notes += "<br />\n";
                }

                return notes;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{ex.Message} {ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// Counts occurrences of "(D.M.C.)" to get number of colors.
        /// </summary>
        private int GetNColors(string pdfContent)
        {
            try
            {
                int nColors = 0;
                string marker = "(D.M.C.)";
                int index = 0;

                while (true)
                {
                    index = pdfContent.IndexOf(marker, index, StringComparison.Ordinal);
                    if (index < 0)
                        break;

                    index++;
                    nColors++;
                }

                return nColors;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{ex.Message} {ex.StackTrace}");
                return 0;
            }
        }

        /// <summary>
        /// Extracts pattern size from notes text, using known delimiters.
        /// Expected: "Design Size: {width} x {height} stitches&lt;br".
        /// </summary>
        private Size GetSize(string notes)
        {
            var size = new Size(0, 0);

            try
            {
                string[] delimiters =
                {
                    "Design Size: ",
                    " x ",
                    "stitches<br"
                };

                string[] split = notes.Split(delimiters, StringSplitOptions.RemoveEmptyEntries);
                if (split.Length < 3)
                    return size;

                string widthStr = split[1];
                string heightStr = split[2];

                size.Width = Convert.ToInt32(widthStr);
                size.Height = Convert.ToInt32(heightStr);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{ex.Message} {ex.StackTrace}");
            }

            return size;
        }
    }
}
