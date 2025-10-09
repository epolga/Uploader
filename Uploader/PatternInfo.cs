using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Windows;
using Size = System.Windows.Size;
using MessageBox = System.Windows.MessageBox;

namespace UploadPatterns
{
    class PatternInfo
    {
        public PatternInfo(string strFilePath)
        {
            ParsePDF(strFilePath);
        }
        string m_strTitle = "";

        public string Title
        {
            get { return m_strTitle; }
         }

        string m_strNotes = "";

        public string Notes
        {
            get { return m_strNotes; }
        }

        string m_strDescription = "";

        public string Description
        {
            get { return m_strDescription; }
        }

        int m_nColors = 0;

        public int NColors
        {
            get { return m_nColors; }
        }

        int m_iWidth = 0;

        public int Width
        {
            get { return m_iWidth; }
            set { m_iWidth = value; }
        }

        int m_iHeight = 0;

        public int Height
        {
            get { return m_iHeight; }
            set { m_iHeight = value; }
        }

        void ParsePDF(string strFilePath)
        {
            try
            {
                string strPDFContent = File.ReadAllText(strFilePath);
                string strTitle = GetTitle(strPDFContent);
                string strNotes = GetNotes(strPDFContent);
                int nColors = GetNColors(strPDFContent);
                Size size = GetSize(strNotes);
                m_strTitle = strTitle;
                m_strNotes = strNotes;
                m_nColors = nColors;
                m_iWidth = (int)size.Width;
                m_iHeight = (int)size.Height;
                m_strDescription = string.Format("{0} x {1} stitches {2} colors", m_iWidth, m_iHeight, nColors);
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format("{0} {1}", ex.Message, ex.StackTrace));
            }


        }

        string GetTitle(string strPDFContent)
        {
            try
            {
                string strBeforeTitle = "-0.0367  Tc 0.0967  Tw (";
                int iBeforeTitleLength = strBeforeTitle.Length;
                string strAfterTitle = ")";
                int iBeforeTitlePosition = strPDFContent.IndexOf(strBeforeTitle);
                string strTitle = strPDFContent.Substring(iBeforeTitlePosition + iBeforeTitleLength);
                int iAfterTitlePosition = strTitle.IndexOf(strAfterTitle);
                strTitle = strTitle.Substring(0, iAfterTitlePosition - 1);
                strTitle = strTitle.Trim();
                return strTitle;
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format("{0} {1}", ex.Message, ex.StackTrace));
            }
            return null;
        }


        string GetNotes(string strPDFContent)
        {
            try
            {
                string strBeforeNotes = "(Notes) Tj";
                int iBeforeNotesLength = strBeforeNotes.Length;
                int nStrings = 7;
                string[] arrBeforeNotesStrings = new string[nStrings];
                int i = 0;
                arrBeforeNotesStrings[i++] = "(aaMaterial Type:";
                arrBeforeNotesStrings[i++] = "(Material Type:";
                arrBeforeNotesStrings[i++] = "(Sewing Count:";
                arrBeforeNotesStrings[i++] = "(Design Size:";
                arrBeforeNotesStrings[i++] = "(Sewn Design Size:";
                arrBeforeNotesStrings[i++] = "(Suggested Material Size:";
                arrBeforeNotesStrings[i++] = "(Stitch Style:";

                int iLength = arrBeforeNotesStrings.Length;

                int iBeforeNotesPosition = strPDFContent.IndexOf(strBeforeNotes);
                string strNotesPart = strPDFContent.Substring(iBeforeNotesPosition + iBeforeNotesLength);
                string strNotes = string.Empty;
                string strAfterString = ")";

                for (i = 0; i < iLength; i++)
                {
                    int iBeforeNotesStringPosition = strNotesPart.IndexOf(arrBeforeNotesStrings[i]);
                    if (iBeforeNotesStringPosition < 0)
                        continue;
                    iBeforeNotesStringPosition++;
                    int iAfterNotesStringPosition = strNotesPart.IndexOf(strAfterString, iBeforeNotesStringPosition);
                    int iNotesStringLength = iAfterNotesStringPosition - iBeforeNotesStringPosition;
                    strNotes += strNotesPart.Substring(iBeforeNotesStringPosition, iNotesStringLength);
                    strNotes += "<br />\n";
                }

                return strNotes;
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format("{0} {1}", ex.Message, ex.StackTrace));
            }
            return null;
        }


        int GetNColors(string strPDFContent)
        {
            try
            {
                int nColors = 0;
                string strColorString = "(D.M.C.)";
                int iIndex = 0;
                while (true)
                {
                    iIndex = strPDFContent.IndexOf(strColorString, iIndex);
                    if (iIndex < 0)
                        break;
                    iIndex++;
                    nColors++;
                }
                return nColors;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(string.Format("{0} {1}", ex.Message, ex.StackTrace));
            }
            return 0;
        }

        System.Windows.Size GetSize(string strNotes)
        {
            Size size = new Size(0, 0);
            try
            {
                string[] strDelimiters = new string[] { "Design Size: ", " x ", "stitches<br" };
                string[] strTmpStrings = strNotes.Split(strDelimiters, StringSplitOptions.RemoveEmptyEntries);
                if (strTmpStrings.Length < 3)
                    return size;
                string strWidth = strTmpStrings[1];
                string strHeight = strTmpStrings[2];
                size.Width = Convert.ToInt32(strWidth);
                size.Height = Convert.ToInt32(strHeight);
                return size;
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format("{0} {1}", ex.Message, ex.StackTrace));
            }

            return size;
        }

        int m_iDesignID = -1;

        public int DesignID 
        {
            set { m_iDesignID = value; }
            get { return m_iDesignID; }
        }

    }
}

