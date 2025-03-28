﻿/*
 * This file is part of the Astral-PE project.
 * Copyright (c) 2025 DosX. All rights reserved.
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 *
 * Astral-PE is a low-level post-compilation PE header mutator (obfuscator) for native
 * Windows x86/x64 binaries. It modifies structural metadata while preserving execution integrity.
 *
 * For source code, updates, and documentation, visit:
 * https://github.com/DosX-dev/Astral-PE
 */

using PeNet;

namespace AstralPE.Obfuscator.Modules {
    public class EntryPointPatcher : IAstralPeModule {

        /// <summary>
        /// Patches the entry point in the PE file.
        /// </summary>
        /// <param name="raw">The raw byte array of the PE file.</param>
        /// <param name="pe">The parsed PE file.</param>
        /// <param name="e_lfanew">Offset to IMAGE_NT_HEADERS.</param>
        /// <param name="optStart">Start offset of the Optional Header.</param>
        /// <param name="sectionTableOffset">Offset to the section table.</param>
        /// <param name="rnd">Random number generator to shuffle bytes (used in the second patch).</param>
        public void Apply(ref byte[] raw, PeFile pe, int e_lfanew, int optStart, int sectionTableOffset, Random rnd) {
            // Ensure the PE file is valid
            if (pe.ImageNtHeaders == null || pe.ImageSectionHeaders == null)
                throw new InvalidPeImageException();

            uint epRva = pe.ImageNtHeaders.OptionalHeader.AddressOfEntryPoint,
                 epOffset = epRva.RvaToOffset(pe.ImageSectionHeaders);

            if (epOffset >= raw.Length)
                throw new Exception("EntryPoint offset is out of file bounds.");

            List<byte> instructions = new List<byte> {
                // PUSH rAX–rDI (0x50–0x57), without 0x54 (PUSH rSP)
                0x50, 0x51, 0x52, 0x53, 0x55, 0x56, 0x57,

                // INC rAX–rDI (0x40–0x47), without 0x44 (INC rSP)
                0x40, 0x41, 0x42, 0x43, 0x45, 0x46, 0x47,

                // DEC rAX–rDI (0x48–0x4F), without 0x4C (DEC rSP)
                0x48, 0x49, 0x4A, 0x4B, 0x4D, 0x4E, 0x4F,

                // Other single-byte instructions
                0x90, // NOP (No operation)
                0xF8, // CLC (Clear carry flag)
                0xF9, // STC (Set carry flag)
                0xFC, // CLD (Clear direction flag)
                0x27, // DAA (Decimal adjust AL after addition)
                0x2F, // DAS (Decimal adjust AL after subtraction)
                0x3F, // AAS (ASCII adjust AL after subtraction)
                0x61, // POPAD (Pop all general-purpose registers)
                0x9C, // PUSHFD (Push EFLAGS to stack)
                0xF3, // REP / REPE / REPZ
                0xF2, // REPNE / REPNZ
                0x2E, // CS segment override
                0x36, // SS segment override
                0x3E, // DS segment override
                0x26, // ES segment override
                0x64, // FS segment override
                0x65  // GS segment override
            };

            // Replace opcode at entry point if it's 0x60 (PUSHAD)
            if (raw[epOffset] == 0x60)
                raw[epOffset] = instructions[rnd.Next(instructions.Count)];

            // Second patch: Shuffle PUSH instructions in UPX64 signature
            if (epOffset + 4 < raw.Length &&
                raw[epOffset + 0] == 0x53 && raw[epOffset + 1] == 0x56 &&
                raw[epOffset + 2] == 0x57 && raw[epOffset + 3] == 0x55) {

                byte[] pushes = { 0x53, 0x56,
                                  0x57, 0x55 };

                for (int i = pushes.Length - 1; i > 0; i--) {
                    int j = rnd.Next(i + 1);
                    (pushes[i], pushes[j]) = (pushes[j], pushes[i]);
                }

                for (int i = 0; i < pushes.Length; i++)
                    raw[epOffset + i] = pushes[i];
            }

            // PATCH: VC++ or MinGW EP stack alignment mutation + NOP/trap sequence replacement
            if (pe.Is64Bit && epOffset + 32 < raw.Length) {
                // Check if entry point starts with VC++-style prologue:
                // sub rsp, imm8; call; add rsp, imm8
                bool isVcStyle =
                     raw[epOffset + 0] == 0x48 && raw[epOffset + 1] == 0x83 && raw[epOffset + 2] == 0xEC &&
                     raw[epOffset + 4] == 0xE8 &&
                     raw[epOffset + 9] == 0x48 && raw[epOffset + 10] == 0x83 && raw[epOffset + 11] == 0xC4;

                // Check if entry point starts with MinGW-style prologue:
                // sub rsp, imm8; mov rax, [rip+...]
                bool isMinGwStyle =
                     raw[epOffset + 0] == 0x48 && raw[epOffset + 1] == 0x83 && raw[epOffset + 2] == 0xEC &&
                     raw[epOffset + 4] == 0x48 && raw[epOffset + 5] == 0x8B && raw[epOffset + 6] == 0x05;

                // Proceed only if one of the patterns matched
                if (isVcStyle || isMinGwStyle) {
                    byte originalStackVal = raw[epOffset + 3];
                    List<byte> stackVariants = new() { 0x28, 0x30, 0x38, 0x40, 0x48, 0x50, 0x58 };
                    stackVariants.Remove(originalStackVal);
                    byte newStackVal = stackVariants[rnd.Next(stackVariants.Count)];

                    raw[epOffset + 3] = newStackVal;

                    if (isVcStyle) {
                        raw[epOffset + 12] = newStackVal;
                    } else if (isMinGwStyle) {
                        for (uint i = epOffset + 7; i < epOffset + 32 && i + 4 < raw.Length; i++) {
                            if (raw[i] == 0x48 && raw[i + 1] == 0x83 && raw[i + 2] == 0xC4 &&
                                raw[i + 3] == originalStackVal && raw[i + 4] == 0xC3) {
                                raw[i + 3] = newStackVal;
                                break;
                            }
                        }
                    }

                    // ---- [ NOP MUTATION for MinGW ] ----
                    if (isMinGwStyle) {
                        for (uint i = epOffset + 12; i < epOffset + 32 && i + 2 < raw.Length; i++) {
                            if (raw[i] == 0x90 && raw[i + 1] == 0x90 && raw[i + 2] == 0x90) {
                                int variant = rnd.Next(4);
                                switch (variant) {
                                    case 0: raw[i] = 0x0F; raw[i + 1] = 0x1F; raw[i + 2] = 0x00; break;
                                    case 1: raw[i] = 0x66; raw[i + 1] = 0x90; raw[i + 2] = 0x90; break;
                                    case 2: raw[i] = 0x90; raw[i + 1] = 0x66; raw[i + 2] = 0x90; break;
                                    case 3: break; // leave 90 90 90 as-is
                                }
                                break;
                            }
                        }
                    }

                    // ---- [ DEAD BYTES GARBAGE AFTER VC++ JMP ] ----
                    if (isVcStyle &&
                        raw[epOffset + 13] == 0xE9 && // jmp
                        raw[epOffset + 18] == 0xCC && raw[epOffset + 19] == 0xCC) { // 2x int3
                        Span<byte> garbage = stackalloc byte[2];
                        rnd.NextBytes(garbage);

                        raw[epOffset + 18] = garbage[0]; // first int3
                        raw[epOffset + 19] = garbage[1]; // second int3
                    }
                }
            }

            if (epOffset > 0 && epOffset < raw.Length && raw[epOffset - 1] == 0 && raw[epOffset - 2] == 0) {
                byte patch = instructions[rnd.Next(instructions.Count)];
                raw[epOffset - 1] = patch;

                // Update EP to point 1 byte earlier
                pe.ImageNtHeaders.OptionalHeader.AddressOfEntryPoint--;
                BitConverter.GetBytes(pe.ImageNtHeaders.OptionalHeader.AddressOfEntryPoint)
                    .CopyTo(raw, optStart + 0x10); // 0x10 = offset of AddressOfEntryPoint in Optional Header
            }
        }
    }
}
