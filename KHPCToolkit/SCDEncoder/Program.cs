﻿using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Xe.BinaryMapper;
using Newtonsoft.Json;
using VAGExtractor;

namespace SCDEncoder
{
    class Program
    {
        private static readonly string TOOLS_PATH = Path.Combine(Path.GetDirectoryName(AppContext.BaseDirectory), "tools");
        private static readonly string RESOURCES_PATH = Path.Combine(Path.GetDirectoryName(AppContext.BaseDirectory), "resources");
        private static readonly string DUMMY_SCD_HEADER_FILE = Path.Combine(Path.GetDirectoryName(AppContext.BaseDirectory), "scd/header.scd");

        private const string TMP_FOLDER_NAME = "tmp";

        // Used to store the mapping between stream names and track index and make sure the output SCD has the track in the proper order
        private static Dictionary<string, Dictionary<int, int>> _streamsMapping = new Dictionary<string, Dictionary<int, int>>();

        private static List<string> SUPPORTED_EXTENSIONS = new List<string>() { ".vsb", /*".vset", ".mdls", /*".dat"*/ };

        private static void Main(string[] args)
        {
            // Check for tools

            if (!File.Exists(@$"{TOOLS_PATH}/vgmstream/test.exe"))
            {
                Console.WriteLine($"Please put test.exe in the tools folder: {TOOLS_PATH}/vgmstream");
                Console.WriteLine("You can find it here: https://vgmstream.org/downloads");
                return;
            }

            if (!File.Exists(@$"{TOOLS_PATH}/adpcmencode/adpcmencode3.exe"))
            {
                Console.WriteLine($"Please put adpcmencode3.exe in the tools folder: {TOOLS_PATH}/adpcmencode");
                Console.WriteLine("You can find it in the Windows 10 SDK: https://developer.microsoft.com/fr-fr/windows/downloads/windows-10-sdk/");
                return;
            }

            if (!File.Exists(@$"{TOOLS_PATH}/sox/sox.exe"))
            {
                Console.WriteLine($"Please put sox.exe in the tools folder: {TOOLS_PATH}/sox");
                Console.WriteLine("You can find it here: https://sourceforge.net/projects/sox/files/sox/");
                return;
            }

            if (args.Length == 3)
            {
                // Parse JSON files in the resources folder to get streams index mapping
                foreach (var file in Directory.GetFiles(RESOURCES_PATH, "*.json"))
                {
                    var content = File.ReadAllText(file);
                    var data = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<int, int>>>(content);

                    foreach (var key in data.Keys)
                    {
                        _streamsMapping[key] = data[key];
                    }
                }

                var input = args[0];
                var outputFolder = args[1];
                var originalScdFile = args[2];

                FileAttributes attr = File.GetAttributes(args[0]);
                if (attr.HasFlag(FileAttributes.Directory))
                {
                    var directory = new DirectoryInfo(args[0]);
                    outputFolder = Path.Combine(Path.GetDirectoryName(AppContext.BaseDirectory), outputFolder, directory.Name);

                    string[] allfiles = Directory.GetFiles(input, "*.*", SearchOption.AllDirectories);

                    foreach (var file in allfiles)
                    {
                        if (SUPPORTED_EXTENSIONS.Contains(Path.GetExtension(file)))
                        {
                            ConvertFile(file, input, outputFolder, originalScdFile);
                        }
                    }
                }
                else
                {
                    ConvertFile(input, Directory.GetParent(input).FullName, outputFolder, originalScdFile);
                }
            }
            else
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("SCDEncoder <file/dir> [<output dir>] [<original scd file>]");
            }
        }

        private static void ConvertFile(string inputFile, string inputFolder, string outputFolder, string originalScd)
        {
            var filename = Path.GetFileName(inputFile);
            var filenameWithoutExtension = Path.GetFileNameWithoutExtension(inputFile);
            var fileExtension = Path.GetExtension(inputFile);

            // Make sure to preserve hierarchy
            var rootFolder = Directory.GetParent(inputFile).FullName;
            var relativePath = rootFolder.Replace(inputFolder, "");
            outputFolder = Path.Combine(outputFolder, inputFolder, relativePath).Replace("\\", "/");

            var tmpFolder = Path.Combine(Path.GetDirectoryName(AppContext.BaseDirectory), TMP_FOLDER_NAME);

            if (Directory.Exists(tmpFolder))
                Directory.Delete(tmpFolder, true);

            Console.WriteLine($"Convert {filename}");

            if (filename == "voice001.vset")
            {
                return;
            }

            var vagFiles = VAGExtractor.VAGTools.ExtractVAGFiles(inputFile, tmpFolder, true, true);

            if (vagFiles.Count == 0)
            {
                return;
            }

            foreach (var file in vagFiles)
            {
                Console.WriteLine($"\t{Path.Combine(relativePath, Path.GetFileName(file))}");
            }

            Directory.CreateDirectory(tmpFolder);
            
            if (!Directory.Exists(outputFolder))
                Directory.CreateDirectory(outputFolder);

            var wavPCMPath = Path.Combine(tmpFolder, $"{filenameWithoutExtension}.wav");
            var scdPath = Path.Combine(tmpFolder, $"{filenameWithoutExtension}.scd");
            var outputFile = Path.Combine(outputFolder, $"{filenameWithoutExtension}.win32.scd");

            var p = new Process();

            var wavPCMFiles = new List<string>();
            var wavADPCMFiles = new List<string>();

            // Convert VAG to WAV
            if (vagFiles.Count > 0)
            {
                foreach (var vagFile in vagFiles)
                {
                    var currentWavPCMPath = Path.Combine(tmpFolder, $"{Path.GetFileNameWithoutExtension(vagFile)}@pcm.wav");
                    var currentWavPCM48Path = Path.Combine(tmpFolder, $"{Path.GetFileNameWithoutExtension(vagFile)}@pcm-48.wav");

                    p.StartInfo.FileName = $@"{TOOLS_PATH}/vgmstream/test.exe";
                    p.StartInfo.Arguments = $"-o \"{currentWavPCMPath}\" \"{vagFile}\"";
                    p.StartInfo.UseShellExecute = false;
                    p.StartInfo.RedirectStandardInput = false;
                    p.Start();
                    p.WaitForExit();

                    // Convert WAV PCM (any sample rate) to WAV PCM with a sample rate of 48kHz
                    p.StartInfo.FileName = $@"{TOOLS_PATH}/sox/sox.exe";
                    p.StartInfo.Arguments = $"\"{currentWavPCMPath}\" --rate 48000 \"{currentWavPCM48Path}\"";
                    p.StartInfo.UseShellExecute = false;
                    p.StartInfo.RedirectStandardInput = false;
                    p.Start();
                    p.WaitForExit();

                    wavPCMFiles.Add(currentWavPCM48Path);
                }
            }
            else
            {
                wavPCMFiles.Add(wavPCMPath);
            }

            foreach (var wavPCMFile in wavPCMFiles)
            {
                var currentWavADPCMPath = Path.Combine(tmpFolder, $"{Path.GetFileNameWithoutExtension(wavPCMFile).Split("@")[0]}.wav");

                // Convert WAV PCM into WAV MS-ADPCM
                p.StartInfo.FileName = $@"{TOOLS_PATH}/adpcmencode/adpcmencode3.exe";
                p.StartInfo.Arguments = $"-b 32 \"{wavPCMFile}\" \"{currentWavADPCMPath}\"";
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardInput = false;
                p.Start();
                p.WaitForExit();

                wavADPCMFiles.Add(currentWavADPCMPath);
            }

            p.Close();

            Dictionary<int, int> mapping = null;

            if (_streamsMapping.ContainsKey(filename))
            {
                mapping = _streamsMapping[filename];
            }
            else
            {
                Console.WriteLine($"Warning: no mapping found for file {filename}");
            }

            CreateSCD(wavADPCMFiles, scdPath, originalScd, mapping);

            File.Copy(scdPath, outputFile, true);

            Console.WriteLine($"Converted {Path.GetFileName(inputFile)} into {Path.GetFileName(outputFile)}. (output: {outputFile})");

#if RELEASE
            Directory.Delete(tmpFolder, true);
#endif
        }

        private static void CreateSCD(List<string> wavFiles, string outputFile, string originalScd, Dictionary<int, int> mapping = null)
        {
            var scd = new SCD(File.OpenRead(originalScd));

            var orderedWavFiles = new SortedList<int, string>();

            if (mapping != null)
            {
                foreach (var key in mapping.Keys)
                {
                    orderedWavFiles.Add(key, wavFiles[mapping[key]]);
                }
            }
            else
            {
                for (int i = 0; i < wavFiles.Count; i++)
                {
                    orderedWavFiles.Add(i, wavFiles[i]);
                }
            }

            if (orderedWavFiles.Count != wavFiles.Count)
            {
                throw new Exception("Some stream names haven't been found!");
            }

            if (scd.StreamsData.Count != wavFiles.Count)
            {
                throw new Exception(
                    "The streams count in the original SCD and the the WAV count doesn't match, " +
                    "please make sure the original SCD you specified correspond to the VSB/WAVs you specified."
                );
            }

            var wavesContent = new List<byte[]>();

            foreach (var wavFile in orderedWavFiles)
            {
                var wavContent = Helpers.StripWavHeader(File.ReadAllBytes(wavFile.Value));
                Helpers.Align(ref wavContent, 0x10);

                wavesContent.Add(wavContent);
            }

            using (var writer = new MemoryStream())
            {
                // Write SCD Header
                var scdHeader = new SCD.SCDHeader()
                {
                    FileVersion = scd.Header.FileVersion,
                    BigEndianFlag = scd.Header.BigEndianFlag,
                    MagicCode = scd.Header.MagicCode,
                    SSCFVersion = scd.Header.SSCFVersion,
                    Padding = scd.Header.Padding,
                    HeaderSize = scd.Header.HeaderSize,
                    // TODO: Fix this, it should be new total file size - table 0 offset position (which correspond to the header size?)
                    TotalFileSize = (uint)wavesContent.Sum(content => content.Length)
                };

                BinaryMapping.WriteObject(writer, scdHeader);

                // Write Table offsets header
                var scdTableOffsetsHeader = new SCD.SCDTableHeader()
                {
                    Table0ElementCount = scd.TablesHeader.Table0ElementCount,
                    Table1ElementCount = scd.TablesHeader.Table1ElementCount,
                    Table2ElementCount = scd.TablesHeader.Table2ElementCount,
                    Table3ElementCount = scd.TablesHeader.Table3ElementCount,
                    Table1Offset = scd.TablesHeader.Table1Offset,
                    Table2Offset = scd.TablesHeader.Table2Offset,
                    Table3Offset = scd.TablesHeader.Table3Offset,
                    Table4Offset = scd.TablesHeader.Table4Offset,
                    Unk14 = scd.TablesHeader.Unk14,
                    Padding = scd.TablesHeader.Padding,
                };

                BinaryMapping.WriteObject(writer, scdTableOffsetsHeader);

                // Write original data from current position to the table 1 offset (before to write all streams offets)
                var data = scd.Data.SubArray((int)writer.Position, (int)(scdTableOffsetsHeader.Table2Offset - writer.Position));
                writer.Write(data);

                // Write stream entries offset
                var streamOffset = (uint)scd.StreamsData[0].Offset;
                var streamHeaderSize = 32;
                var streamsOffsets = new List<uint>();

                for (int i = 0; i < wavesContent.Count; i++)
                {
                    var wavContent = wavesContent[i];
                    writer.Write(BitConverter.GetBytes(streamOffset));

                    streamsOffsets.Add(streamOffset);

                    streamOffset += (uint)(wavContent.Length + (streamHeaderSize + scd.StreamsData[i].ExtraData.Length));
                }

                // Write the original data from current stream position to the start of the first stream header
                data = scd.Data.SubArray((int)writer.Position, (int)(streamsOffsets[0] - writer.Position));
                writer.Write(data);

                // Write data for each stream entry
                for (int i = 0; i < scd.StreamsData.Count; i++)
                {
                    var streamData = scd.StreamsData[i];
                    var wavFile = wavFiles[i];
                    var wavContent = wavesContent[i];

                    var waveFileInfo = new WaveFileReader(wavFile);

                    var newStreamHeader = new SCD.StreamHeader
                    {
                        AuxChunkCount = streamData.Header.AuxChunkCount,
                        ChannelCount = (uint)waveFileInfo.WaveFormat.Channels,
                        Codec = streamData.Header.Codec,
                        ExtraDataSize = streamData.Header.ExtraDataSize,
                        LoopStart = streamData.Header.LoopStart,
                        LoopEnd = streamData.Header.LoopEnd,
                        SampleRate = (uint)waveFileInfo.WaveFormat.SampleRate,
                        StreamSize = (uint)wavContent.Length
                    };

                    // Write stream header
                    BinaryMapping.WriteObject(writer, newStreamHeader);
                    // Write stream extra data
                    writer.Write(streamData.ExtraData);
                    // Write stream audio data
                    writer.Write(wavContent);

                    waveFileInfo.Close();
                }

                File.WriteAllBytes(outputFile, writer.ReadAllBytes());

                // Check the new SCD is correct
                var newScd = new SCD(File.OpenRead(outputFile));
            }
        }
    }
}