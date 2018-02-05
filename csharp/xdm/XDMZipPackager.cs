/* 
 Copyright (c) 2010, Direct Project
 All rights reserved.

 Authors:
    Arien Malec
  
Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:

Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.
Neither the name of The Direct Project (directproject.org) nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.
THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 
*/
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

using Health.Direct.Common.Metadata;
using Health.Direct.Xd;
using System.IO;

namespace Health.Direct.Xdm
{
    public class XDMZipPackager : IPackager<ZipArchive>
    {
        // Use Default
        private XDMZipPackager() { }


        private static readonly XDMZipPackager m_Instance = new XDMZipPackager();

        /// <summary>
        /// The default instance
        /// </summary>
        public static XDMZipPackager Default
        {
            get
            {
                return m_Instance;
            }
        }

        /// <summary>
        /// Unpackages an XDM-encoded zip file
        /// </summary>
        public DocumentPackage Unpackage(ZipArchive z)
        {
            DocumentPackage package;
            package = ReadMetadata(z);
            return package;
        }

        private DocumentPackage ReadMetadata(ZipArchive z)
        {
            ZipArchiveEntry metadataEntry = LocateMetadataFile(z);
            XDocument metadataDoc = ExtractMetadataFile(metadataEntry);
            DocumentPackage package = XDMetadataConsumer.Consume(metadataDoc.Root);
            string[] dirParts = metadataEntry.FullName.Split('/');
            string submissionSetDir = String.Format("{0}/{1}", dirParts[0], dirParts[1]);
            foreach (DocumentMetadata doc in package.Documents)
            {
                string docPath = String.Format("{0}/{1}", submissionSetDir, doc.Uri);
                byte[] bytes = ExtractDocumentBytes(z, docPath);
                doc.SetDocument(bytes);
            }

            return package;
        }

        private byte[] ExtractDocumentBytes(ZipArchive z, string path)
        {
            ZipArchiveEntry docEntry = z.GetEntry(path);
            if (docEntry == null) throw new XdmException(XdmError.FileNotFound, String.Format("File {0} was not located in the archive", path));
            using (Stream stream = docEntry.Open())
            {
                return stream.ReadAllBytes();
            }
        }

        private XDocument ExtractMetadataFile(ZipArchiveEntry e)
        {
            XDocument metadataDoc;
            using (Stream docStream = e.Open())
            {
                using (TextReader reader = new StreamReader(docStream))
                {
                    metadataDoc = XDocument.Load(reader);
                }
            }
            return metadataDoc;
        }


        private ZipArchiveEntry LocateMetadataFile(ZipArchive z)
        {
            IEnumerable<ZipArchiveEntry> subFiles = z.Entries.Where(e => e.FullName.StartsWith(XDMStandard.MainDirectory));
            IEnumerable<ZipArchiveEntry> metadataFiles = subFiles
                .Where(e => e.FullName.EndsWith(XDMStandard.MetadataFilename) &&
                    e.FullName.Split('/').Count() == 3);
            if (metadataFiles.Count() == 0) throw new XdmException(XdmError.NoMetadataFile);
            if (metadataFiles.Count() > 1) throw new NotImplementedException("Multiple submission sets not supported");
            return metadataFiles.First();
        }



        /// <summary>
        /// Packages a <see cref="DocumentPackage"/> as an XDM zip file
        /// </summary>
        public ZipArchive Package(DocumentPackage package)
        {
            // ZipArchive can't do any read operations while in Create mode, so we have to open a stream,
            // have a ZipArchive create into that stream, then reset the stream and point a new ZipArchive (in read mode) at it.
            MemoryStream stream = new MemoryStream();
            Package(package, stream);
            stream.Seek(0, SeekOrigin.Begin);
            return new ZipArchive(stream, ZipArchiveMode.Read, false);
        }

        /// <summary>
        /// Packages a <see cref="DocumentPackage"/> as an XDM and writes it to the given stream.
        /// </summary>
        public void Package(DocumentPackage package, Stream stream)
        {
            using (ZipArchive z = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                AddDocuments(package, z); // Alters URI by side effect
                AddManifests(package, z);
                AddMetadata(package, z);
            }
        }

        private void AddManifests(DocumentPackage package, ZipArchive z)
        {
            AddIndex(package, z);
            AddReadme(z);
        }

        private void AddReadme(ZipArchive z)
        {
            AddZipArchiveEntry(z, XDMStandard.ReadmeFilename, XDMStandard.ReadmeFileString);
        }

        private void AddIndex(DocumentPackage package, ZipArchive z)
        {
            AddZipArchiveEntry(z, XDMStandard.IndexHtmFile, GenerateIndexFile(package));
        }

        private void AddMetadata(DocumentPackage package, ZipArchive z)
        {
            
            StringBuilder sb = new StringBuilder();
            using (StringWriter w = new StringWriter(sb))
            {
                package.Generate().Save(w);
            }

            AddZipArchiveEntry(z, XDMStandard.DefaultMetadataFilePath, sb.ToString());
        }

        private void AddDocuments(DocumentPackage package, ZipArchive z)
        {
            int i = 1;
            foreach (DocumentMetadata doc in package.Documents)
            {
                if (doc.DocumentBytes == null) throw new XdMetadataException(XdError.MissingDocumentBytes);
                string suffix = i.ToString("000");
                string name = XDMStandard.DocPrefix + suffix;
                string path = String.Format("{0}/{1}/{2}", XDMStandard.MainDirectory, XDMStandard.DefaultSubmissionSet, name);
                doc.Uri = name;
                AddZipArchiveEntry(z, path, doc.DocumentBytes);
            }
        }

        private string GenerateIndexFile(DocumentPackage package)
        {
            var liElts = from d in package.Documents
                         select new XElement("li",
                             new XElement("a", d.Title,
                                 new XAttribute("href", String.Format("{0}/{1}/{2}", XDMStandard.MainDirectory, XDMStandard.DefaultSubmissionSet, d.Uri))));
            XDocument index = new XDocument(
                new XDocumentType("html", "-//W3C//DTD XHTML Basic 1.1//EN", "http://www.w3.org/TR/xhtml-basic/xhtml-basic11.dtd", null),
                new XElement("html",
                    new XElement("head",
                        new XElement("title", "Content index")),
                    new XElement("body",
                        new XElement("h2", "Content index"),
                        new XElement("ul", liElts))));

            return index.ToString();
        }

        /// <summary>
        /// Adds data as an entry to a <see cref="ZipArchive"/>.
        /// </summary>
        /// <param name="zip">The <see cref="ZipArchive"/> to add to.</param>
        /// <param name="path">The path, relative to the root of the archive, to add the entry.</param>
        /// <param name="content">The content of the new entry.</param>
        private void AddZipArchiveEntry(ZipArchive zip, string path, byte[] content)
        {
            var entry = zip.CreateEntry(path);
            using (var stream = entry.Open())
            {
                stream.Write(content, 0, content.Length);
            }
        }

        /// <summary>
        /// Adds text as an entry to a <see cref="ZipArchive"/>.
        /// </summary>
        /// <param name="zip">The <see cref="ZipArchive"/> to add to.</param>
        /// <param name="path">The path, relative to the root of the archive, to add the entry.</param>
        /// <param name="text">The content of the new entry.</param>
        private void AddZipArchiveEntry(ZipArchive zip, string path, string text)
        {
            var entry = zip.CreateEntry(path);
            using (var stream = entry.Open())
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(text);
            }
        }
    }
}
