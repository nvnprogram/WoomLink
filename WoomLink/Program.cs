using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using WoomLink.Ex;
using WoomLink.Ex.sead;
using WoomLink.sead;
using WoomLink.xlink2;
using WoomLink.xlink2.File;
using WoomLink.xlink2.Properties;

namespace WoomLink
{
    internal class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                PrintUsage();
                return;
            }

            Console.OutputEncoding = Encoding.UTF8;

            string mode = args[0];
            switch (mode)
            {
                case "convert":
                    RunConvert(args);
                    break;
                case "rebuild":
                    RunRebuild(args);
                    break;
                case "legacy":
                    RunLegacy(args);
                    break;
                default:
                    PrintUsage();
                    break;
            }
        }

        static void PrintUsage()
        {
            Console.Error.WriteLine("WoomLink - XLink binary converter");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Convert binary to text:");
            Console.Error.WriteLine("  WoomLink convert <input.belnk|bslnk> [--output <file.txt>] [--actors <ActorDb.yaml>]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Rebuild text to binary:");
            Console.Error.WriteLine("  WoomLink rebuild <input.txt> [--output <file.belnk|bslnk>]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Legacy mode (old WoomLink behavior):");
            Console.Error.WriteLine("  WoomLink legacy <elink-file> <slink-file> [--user <name>]");
        }

        static void RunConvert(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: WoomLink convert <input> [--output <file>] [--actors <ActorDb.yaml>]");
                return;
            }

            string input = args[1];
            string? output = null;
            string? actorsPath = null;
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--output" && i + 1 < args.Length)
                    output = args[++i];
                else if (args[i] == "--actors" && i + 1 < args.Length)
                    actorsPath = args[++i];
            }

            var data = File.ReadAllBytes(input);
            var reader = new Converter.XLinkBinaryReader();
            var model = reader.Read(data);

            Dictionary<uint, string>? actorNames = null;
            if (actorsPath != null)
            {
                actorNames = Converter.ActorYamlParser.Parse(actorsPath);
                Console.Error.WriteLine($"Loaded {actorNames.Count} actor names from {actorsPath}");
            }

            System.IO.TextWriter writer;
            if (output != null)
            {
                writer = new StreamWriter(output, false, new UTF8Encoding(false));
            }
            else
            {
                writer = Console.Out;
            }

            var textWriter = new Converter.XLinkTextWriter(writer, actorNames);
            textWriter.Write(model);

            if (output != null)
            {
                writer.Dispose();
                Console.Error.WriteLine($"Wrote {output}");
            }
        }

        static void RunRebuild(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: WoomLink rebuild <input.txt> [--output <file>]");
                return;
            }

            string input = args[1];
            string? output = null;
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--output" && i + 1 < args.Length)
                    output = args[++i];
            }

            using var textReader = new StreamReader(input);
            var parser = new Converter.XLinkTextReader();
            var model = parser.Read(textReader);

            var writer = new Converter.XLinkBinaryWriter();
            var binary = writer.Write(model);

            output ??= Path.ChangeExtension(input, ".bin");
            File.WriteAllBytes(output, binary);
            Console.Error.WriteLine($"Wrote {output} ({binary.Length} bytes)");
        }

        static void RunLegacy(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Usage: WoomLink legacy <elink-file> <slink-file> [--user <name>]");
                return;
            }

            var elinkPath = args[1];
            var slinkPath = args[2];
            string? userName = null;

            for (int i = 3; i < args.Length; i++)
            {
                if (args[i] == "--user" && i + 1 < args.Length)
                    userName = args[++i];
            }

            var edata = LoadRawDataOntoHeap(new FileInfo(elinkPath));
            var sdata = LoadRawDataOntoHeap(new FileInfo(slinkPath));

            var esystem = SystemELink.GetInstance();
            var ssystem = SystemSLink.GetInstance();
            const int eventPoolNum = 96;
            esystem.Initialize(null, eventPoolNum);
            ssystem.Initialize(eventPoolNum);

            void SetupSystem(xlink2.System system, Pointer<byte> resource)
            {
                var r = system.LoadResource(resource.PointerValue);
                Debug.Assert(r);
            }

            SetupSystem(esystem, edata);
            SetupSystem(ssystem, sdata);

            if (userName != null)
            {
                Console.WriteLine(PrintUserByName(esystem, userName));
            }
            else
            {
                PrintAllUsers(esystem);
                PrintAllUsers(ssystem);
            }
        }

        private static Pointer<byte> LoadFileOntoHeap(FileInfo info, byte[]? dict = null)
        {
            var name = info.Name;
            if (name.EndsWith(".zs", StringComparison.OrdinalIgnoreCase))
                return LoadZstdCompressedDataOntoHeap(info, dict);
            if (name.EndsWith(".szs", StringComparison.OrdinalIgnoreCase))
                return LoadYaz0SarcFileOntoHeap(info);
            return LoadRawDataOntoHeap(info);
        }

        private static string? PrintUserByIndex(xlink2.System system, int index)
        {
            ref var param = ref system.ResourceBuffer.RSP;
            var user = param.UserDataPointersSpan[index];

            ResourceParamCreator.CreateUserBinParam(out var userParam, user, in system.GetParamDefineTable());
            var writer = new UserPrinter();
            writer.Print(system, in param.Common, in userParam);
            return writer.Writer.ToString()!;
        }

        private static string? PrintUserByName(xlink2.System system, string name)
        {
            ref var param = ref system.ResourceBuffer.RSP;

            var idx = Utils.BinarySearch<uint, uint>(param.UserDataHashesSpan, HashCrc32.CalcStringHash(name));
            if (idx < 0)
                return null;

            return PrintUserByIndex(system, idx);
        }

        private static void SaveUsersFromNames(xlink2.System system, StreamReader reader, DirectoryInfo outDir)
        {
            outDir.Create();
            string userName;
            while ((userName = reader.ReadLine()) != null)
            {
                var text = PrintUserByName(system, userName);
                if (text == null)
                {
                    Console.WriteLine($"Failed to read {userName}");
                    continue;
                }

                var outFi = outDir.GetFile($"{userName}.txt");
                File.WriteAllText(outFi.FullName, text, Encoding.UTF8);
            }
        }

        private static void PrintAllUsers(xlink2.System system)
        {
            ref var param = ref system.ResourceBuffer.RSP;
            for (var i = 0; i < param.NumUser; i++)
            {
                var name = param.UserDataHashesSpan[i];
                Console.WriteLine($"{name:X8}");
                Console.WriteLine(PrintUserByIndex(system, i));
            }
        }

        private static Pointer<byte> LoadRawDataOntoHeap(FileInfo info)
        {
            using var stream = info.OpenRead();
            var ptr = Ex.FakeHeap.AllocateT<byte>((SizeT)stream.Length);
            stream.Read(ptr.AsSpan((int)stream.Length));
            return ptr;
        }

        private static T LoadZstdCompressedDataTo<T>(FileInfo info, byte[]? dict, Func<Stream, SizeT, T> callback)
        {
            const int ZSTD_frameHeaderSize_max = 18;
            var frameHeader = new byte[ZSTD_frameHeaderSize_max];
            using var stream = info.OpenRead();
            stream.Read(frameHeader);
            stream.Position = 0;

            Stream decompressStream;
            if (dict != null)
            {
                decompressStream = new ZstdNet.DecompressionStream(stream, new ZstdNet.DecompressionOptions(dict));
            }
            else
            {
                decompressStream = new ZstdNet.DecompressionStream(stream);
            }

            using (decompressStream)
            {
                var decompressedSize = ZstdNet.Decompressor.GetDecompressedSize(frameHeader);
                return callback(decompressStream, (SizeT)decompressedSize);
            }
        }

        private static Pointer<byte> LoadZstdCompressedDataOntoHeap(FileInfo info, byte[]? dict = null)
        {
            return LoadZstdCompressedDataTo(info, dict, (stream, length) =>
            {
                var ptr = FakeHeap.AllocateT<byte>(length);
                stream.Read(ptr.AsSpan((int)length));
                return ptr;
            });
        }

        private static byte[] LoadZstdCompressedData(FileInfo info, byte[]? dict = null)
        {
            return LoadZstdCompressedDataTo(info, dict, (stream, length) =>
            {
                var bytes = new byte[length];
                stream.Read(bytes);
                return bytes;
            });
        }

        private static byte[] LoadYaz0CompressedData(FileInfo info)
        {
            using var stream = info.OpenRead();
            return Yaz0.Decompress(stream);
        }

        private static Span<byte> LoadYaz0SarcFile(FileInfo info)
        {
            var decompressed = LoadYaz0CompressedData(info);
            var sarc = new Sarc(decompressed);
            if (sarc.FileNodes.Length != 1)
                throw new Exception("Invalid file count in SARC!");

            return sarc.OpenFile(0);
        }

        private static Pointer<byte> LoadYaz0SarcFileOntoHeap(FileInfo info)
        {
            var data = LoadYaz0SarcFile(info);
            var ptr = FakeHeap.AllocateT<byte>(data.Length);
            data.CopyTo(ptr.AsSpan(data.Length));
            return ptr;
        }

        private static Pointer<byte> LoadYaz0FileOntoHeap(FileInfo info)
        {
            using var stream = info.OpenRead();
            if (!Yaz0.IsYaz0(stream))
                throw new Exception("Not Yaz0!");

            Yaz0.Yaz0Header header = new();
            using (stream.TemporarySeek())
            {
                stream.Read(Utils.AsSpan(ref header));
            }

            var ptr = FakeHeap.AllocateT<byte>((SizeT)header.DecompressedSize.ByteReversed());
            Yaz0.DecompressTo(stream, ptr.AsSpan((int)header.DecompressedSize.ByteReversed()));
            return ptr;
        }
    }
}
