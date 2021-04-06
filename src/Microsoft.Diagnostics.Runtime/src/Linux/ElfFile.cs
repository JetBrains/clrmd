﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.IO;
using System.Linq;

namespace Microsoft.Diagnostics.Runtime.Linux
{
    internal class ElfFile
    {
        private readonly long _position;
        private readonly bool _virtual;

        private Reader? _virtualAddressReader;
        private ImmutableArray<ElfNote> _notes;
        private ImmutableArray<ElfProgramHeader> _programHeaders;
        private ImmutableArray<byte> _buildId;
        private ElfDynamicSection? _dynamicSection;

        public Reader Reader { get; }

        public IElfHeader Header { get; }

        public ImmutableArray<ElfNote> Notes
        {
            get
            {
                LoadNotes();
                return _notes;
            }
        }

        public ImmutableArray<ElfProgramHeader> ProgramHeaders
        {
            get
            {
                LoadProgramHeaders();
                return _programHeaders;
            }
        }

        public Reader VirtualAddressReader
        {
            get
            {
                CreateVirtualAddressReader();
                return _virtualAddressReader!;
            }
        }

        public ElfDynamicSection DynamicSection
        {
            get
            {
                if (_dynamicSection == null)
                {
                    foreach (ElfProgramHeader? header in ProgramHeaders)
                    {
                        if (header.Type == ElfProgramHeaderType.Dynamic)
                        {
                            _dynamicSection = new ElfDynamicSection(
                                Reader, Header.Is64Bit, _position + (_virtual ? header.VirtualAddress : header.FileOffset), _virtual ? header.VirtualSize : header.FileSize);
                            break;
                        }
                    }
                }
                return _dynamicSection ?? throw new InvalidDataException("No dynamic segment found");
            }
        }

        public ImmutableArray<byte> BuildId
        {
            get
            {
                if (!_buildId.IsDefault)
                    return _buildId;

                if (Header.ProgramHeaderOffset != 0 && Header.ProgramHeaderEntrySize > 0 && Header.ProgramHeaderCount > 0)
                {
                    try
                    {
                        foreach (ElfNote note in Notes)
                        {
                            if (note.Type == ElfNoteType.PrpsInfo && note.Name.Equals("GNU"))
                            {
                                byte[] buildId = new byte[note.Header.ContentSize];
                                note.ReadContents(0, buildId);
                                return _buildId = buildId.AsImmutableArray();
                            }
                        }
                    }
                    catch (IOException)
                    {
                    }
                }

                return default;
            }
        }

        public ElfFile(Reader reader, long position = 0, bool isVirtual = false)
        {
            Reader = reader;
            _position = position;
            _virtual = isVirtual;

            if (isVirtual)
                _virtualAddressReader = reader;

            ElfHeaderCommon common;
            try
            {
                common = reader.Read<ElfHeaderCommon>(position);
            }
            catch (IOException e)
            {
                throw new InvalidDataException($"{reader.DataSource.Name ?? "This coredump"} does not contain a valid ELF header.", e);
            }

            Header = common.GetHeader(reader, position)!;
            if (Header is null)
                throw new InvalidDataException($"{reader.DataSource.Name ?? "This coredump"} does not contain a valid ELF header.");
        }

        internal ElfFile(IElfHeader header, Reader reader, long position = 0, bool isVirtual = false)
        {
            Reader = reader;
            _position = position;
            _virtual = isVirtual;

            if (isVirtual)
                _virtualAddressReader = reader;

            Header = header;
        }

        private void CreateVirtualAddressReader()
        {
            _virtualAddressReader ??= new Reader(new ElfVirtualAddressSpace(ProgramHeaders, Reader.DataSource));
        }

        private void LoadNotes()
        {
            if (!_notes.IsDefault)
                return;

            LoadProgramHeaders();

            ImmutableArray<ElfNote>.Builder notes = ImmutableArray.CreateBuilder<ElfNote>();
            foreach (ElfProgramHeader programHeader in _programHeaders)
            {
                if (programHeader.Type == ElfProgramHeaderType.Note)
                {
                    Reader reader = new Reader(programHeader.AddressSpace);
                    long position = 0;
                    while (position < reader.DataSource.Length)
                    {
                        ElfNote note = new ElfNote(reader, position);
                        notes.Add(note);

                        position += note.TotalSize;
                    }
                }
            }

            _notes = notes.MoveOrCopyToImmutable();
        }

        private void LoadProgramHeaders()
        {
            if (!_programHeaders.IsDefault)
                return;

            // Calculate the loadBias. It is usually just the base address except for some executable modules.
            long loadBias = _position;
            if (loadBias > 0)
            {
                for (int i = 0; i < Header.ProgramHeaderCount; i++)
                {
                    var header= new ElfProgramHeader(Reader, Header.Is64Bit, _position + Header.ProgramHeaderOffset + i * Header.ProgramHeaderEntrySize, 0, _virtual);
                    if (header.Type == ElfProgramHeaderType.Load && header.FileOffset == 0)
                    {
                        loadBias -= header.VirtualAddress;
                    }
                }
            }

            // Build the program segments using the load bias
            ImmutableArray<ElfProgramHeader>.Builder programHeaders = ImmutableArray.CreateBuilder<ElfProgramHeader>(Header.ProgramHeaderCount);
            programHeaders.Count = programHeaders.Capacity;

            for (int i = 0; i < programHeaders.Count; i++)
                programHeaders[i] = new ElfProgramHeader(Reader, Header.Is64Bit, _position + Header.ProgramHeaderOffset + i * Header.ProgramHeaderEntrySize, loadBias, _virtual);

            _programHeaders = programHeaders.MoveToImmutable();
        }
    }
}