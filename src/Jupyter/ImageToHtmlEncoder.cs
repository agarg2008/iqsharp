
using Microsoft.Jupyter.Core;
using System.Collections.Generic;
using System.IO;
using System.Text;

public class ImageToHtmlEncoder : IResultEncoder
{
    public string MimeType => MimeTypes.Html;
    public EncodedData? Encode(object displayable)
    {
        if (displayable == null) return null;

        if (displayable is string)
        {
            return null;
        }
        if (displayable is ImageDataWrapper symbol)
        {
            string base64encoded;
            if (!string.IsNullOrWhiteSpace(symbol.imageFileName))
            {
                List<byte> byteResult = new List<byte>();
                using (FileStream stream = File.Open(symbol.imageFileName, FileMode.Open))
                {
                    byte[] b = new byte[1024];
                    UTF8Encoding temp = new UTF8Encoding(true);

                    while (stream.Read(b, 0, b.Length) > 0)
                    {
                        byteResult.AddRange(b);
                    }
                }
                base64encoded = System.Convert.ToBase64String(byteResult.ToArray());
            }
            else
            {
                base64encoded = symbol.data;
            }
            string result = $"<div><img src=\"data:image/png;base64,{base64encoded}\"/></div>";
            return result.ToEncodedData();
        }
        return null;
    }

    public class ImageDataWrapper
    {
        public string data;
        public string imageFileName;
    }
}