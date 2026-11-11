// File: PM5000WaveformParser.cs
using System;
using System.IO;
using System.Numerics;
using FTOptix.AuditSigning;
using FTOptix.DataLogger;
using FTOptix.SerialPort;

namespace OptixWaveformParser
{
    public class PM5000WaveformParser
    {
        private const int BYTES_PER_CYCLE = 3484;
        private const int HEADER_SIZE = 64;
        private const int MAX_CYCLES_PER_SEGMENT = 60;

        public byte[] RawData { get; private set; }
        public DateTime CaptureTime { get; private set; }
        public int MicrosecondOfCapture { get; private set; }
        public int TotalCycles { get; private set; }
        public int TotalSegments { get; private set; }

        /// <summary>
        /// Load waveform from binary .wfm file
        /// </summary>
        public void LoadFromFile(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Waveform file not found: {filePath}");

            RawData = File.ReadAllBytes(filePath);
            
            if (RawData.Length < HEADER_SIZE)
                throw new InvalidDataException("File is too small to be a valid waveform");

            ExtractHeader();
            TotalCycles = (RawData.Length - HEADER_SIZE) / BYTES_PER_CYCLE;
            TotalSegments = (TotalCycles - 1) / MAX_CYCLES_PER_SEGMENT + 1;
            
            // Parse datetime from filename
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            ParseDateTimeFromFilename(fileName);
        }

        /// <summary>
        /// Extract waveform segment (up to 60 cycles)
        /// </summary>
        public WaveformSegment GetSegment(int segmentNumber)
        {
            if (segmentNumber < 1 || segmentNumber > TotalSegments)
                throw new ArgumentException($"Segment number must be between 1 and {TotalSegments}");

            var segment = new WaveformSegment
            {
                SegmentNumber = segmentNumber,
                TotalSegments = TotalSegments,
                TotalWaveformCycles = TotalCycles,
                CaptureTime = CaptureTime,
                MicrosecondOfCapture = MicrosecondOfCapture
            };

            // Determine cycles in this segment
            if (segmentNumber < TotalSegments)
                segment.SegmentCycles = MAX_CYCLES_PER_SEGMENT;
            else
                segment.SegmentCycles = TotalCycles - (TotalSegments - 1) * MAX_CYCLES_PER_SEGMENT;

            // Initialize arrays
            segment.SecondOfCycle = new uint[segment.SegmentCycles];
            segment.MicrosecondOfCycle = new uint[segment.SegmentCycles];
            segment.FrequencyOfCycle = new float[segment.SegmentCycles];
            segment.DeltaTMilliseconds = new double[segment.SegmentCycles * 256];
            segment.WaveformValues = new double[8, segment.SegmentCycles * 256];

            // Process each cycle in segment
            int offset = (segmentNumber - 1) * MAX_CYCLES_PER_SEGMENT * BYTES_PER_CYCLE + HEADER_SIZE;
            
            float[,] fMag = new float[8, 128];
            float[,] fAngle = new float[8, 128];
            float[,] dataPoints = new float[8, 256];

            for (int cycle = 0; cycle < segment.SegmentCycles; cycle++)
            {
                // Extract timestamp (8 bytes)
                uint seconds = BitConverter.ToUInt32(RawData, offset);
                uint nanoseconds = BitConverter.ToUInt32(RawData, offset + 4);
                segment.SecondOfCycle[cycle] = seconds;
                segment.MicrosecondOfCycle[cycle] = nanoseconds / 1000;
                offset += 8;

                // Extract frequency (4 bytes)
                segment.FrequencyOfCycle[cycle] = BitConverter.ToSingle(RawData, offset);
                offset += 4;

                // ✅ FIX: Extract compressed magnitude data (1864 bytes = 8 channels × 233 bytes)
                byte[,] compressedMag = new byte[8, 233];
                for (int ch = 0; ch < 8; ch++)
                {
                    for (int b = 0; b < 233; b++)
                    {
                        compressedMag[ch, b] = RawData[offset++];
                    }
                }

                // ✅ FIX: Extract compressed angle data (1608 bytes = 8 channels × 201 bytes)
                byte[,] compressedAngle = new byte[8, 201];
                for (int ch = 0; ch < 8; ch++)
                {
                    for (int b = 0; b < 201; b++)
                    {
                        compressedAngle[ch, b] = RawData[offset++];
                    }
                }

                // Decompress frequency domain data
                Decompression(compressedMag, compressedAngle, fMag, fAngle);

                // Convert to time domain via IFFT
                InverseFFTCalculation(fMag, fAngle, dataPoints);

                // Calculate timestamps for each sample
                double cycleTime = (double)seconds + (double)segment.MicrosecondOfCycle[cycle] / 1_000_000.0;
                double samplePeriod = (segment.FrequencyOfCycle[cycle] > 0) 
                    ? 1.0 / (256.0 * segment.FrequencyOfCycle[cycle]) 
                    : 1.0 / (256.0 * 60.0); // Default to 60Hz

                for (int sample = 0; sample < 256; sample++)
                {
                    int idx = sample + cycle * 256;
                    segment.DeltaTMilliseconds[idx] = cycleTime * 1000.0;
                    cycleTime += samplePeriod;

                    // ✅ FIX: Copy waveform values CORRECTLY for each channel
                    for (int ch = 0; ch < 8; ch++)
                    {
                        segment.WaveformValues[ch, idx] = dataPoints[ch, sample];
                        
                        // Track max values
                        double absValue = Math.Abs(dataPoints[ch, sample]);
                        if (ch < 4) // Voltage channels
                        {
                            if (absValue > segment.MaxVoltage)
                                segment.MaxVoltage = absValue;
                        }
                        else // Current channels
                        {
                            if (absValue > segment.MaxCurrent)
                                segment.MaxCurrent = absValue;
                        }
                    }
                }
            }

            return segment;
        }

        /// <summary>
        /// Decompress frequency domain magnitude and angle data
        /// </summary>
        private void Decompression(byte[,] cMag, byte[,] cAngle, float[,] fMag, float[,] fAngle)
        {
            byte[] tempBytes = new byte[4];
            const uint EXPONENT_BIAS = 15;

            for (int ch = 0; ch < 8; ch++)
            {
                // ===== DECOMPRESS MAGNITUDE =====
                
                // First 16 bins are full 32-bit floats (64 bytes)
                for (int bin = 0; bin < 16; bin++)
                {
                    tempBytes[0] = cMag[ch, bin * 4];
                    tempBytes[1] = cMag[ch, bin * 4 + 1];
                    tempBytes[2] = cMag[ch, bin * 4 + 2];
                    tempBytes[3] = cMag[ch, bin * 4 + 3];
                    fMag[ch, bin] = BitConverter.ToSingle(tempBytes, 0);
                }

                // Reference exponent at byte 64
                int magOffset = 64;
                uint refExponent = cMag[ch, magOffset];
                magOffset++;

                // Bins 16-127 are compressed (12-bit each, 169 bytes)
                for (int bin = 16; bin < 128; bin++)
                {
                    uint compressedValue = ExtractCompressed12Bit(cMag, ch, ref magOffset, bin);
                    
                    if (compressedValue == 0)
                    {
                        fMag[ch, bin] = 0.0f;
                    }
                    else
                    {
                        uint sign = (compressedValue & 0x800) != 0 ? 1u : 0u;
                        uint exponentDiff = (compressedValue >> 7) & 0xF;
                        uint exponent = refExponent - (EXPONENT_BIAS - exponentDiff);
                        uint mantissa = (compressedValue & 0x7F) << 16;
                        
                        uint floatBits = (sign << 31) | (exponent << 23) | mantissa;
                        fMag[ch, bin] = BitConverter.ToSingle(BitConverter.GetBytes(floatBits), 0);
                    }
                }

                // ===== DECOMPRESS ANGLE =====
                
                int angleOffset = 0;
                refExponent = cAngle[ch, angleOffset];
                angleOffset++;

                // First 16 angle bins (16-bit compressed, 32 bytes)
                for (int bin = 0; bin < 16; bin++)
                {
                    uint compressedValue = (uint)cAngle[ch, angleOffset] | ((uint)cAngle[ch, angleOffset + 1] << 8);
                    angleOffset += 2;

                    if (compressedValue == 0)
                    {
                        fAngle[ch, bin] = 0.0f;
                    }
                    else
                    {
                        uint sign = (compressedValue & 0x8000) != 0 ? 1u : 0u;
                        uint exponentDiff = (compressedValue >> 11) & 0xF;
                        uint exponent = refExponent - (EXPONENT_BIAS - exponentDiff);
                        uint mantissa = (compressedValue & 0x7FF) << 12;
                        
                        uint floatBits = (sign << 31) | (exponent << 23) | mantissa;
                        fAngle[ch, bin] = BitConverter.ToSingle(BitConverter.GetBytes(floatBits), 0);
                    }
                }

                // Bins 16-127 (12-bit compressed, 168 bytes)
                for (int bin = 16; bin < 128; bin++)
                {
                    uint compressedValue = ExtractCompressed12Bit(cAngle, ch, ref angleOffset, bin);
                    
                    if (compressedValue == 0)
                    {
                        fAngle[ch, bin] = 0.0f;
                    }
                    else
                    {
                        uint sign = (compressedValue & 0x800) != 0 ? 1u : 0u;
                        uint exponentDiff = (compressedValue >> 7) & 0xF;
                        uint exponent = refExponent - (EXPONENT_BIAS - exponentDiff);
                        uint mantissa = (compressedValue & 0x7F) << 16;
                        
                        uint floatBits = (sign << 31) | (exponent << 23) | mantissa;
                        fAngle[ch, bin] = BitConverter.ToSingle(BitConverter.GetBytes(floatBits), 0);
                    }
                }
            }
        }

        /// <summary>
        /// Extract 12-bit compressed value from byte array
        /// Handles nibble packing for odd/even bins
        /// </summary>
        private uint ExtractCompressed12Bit(byte[,] data, int channel, ref int offset, int bin)
        {
            uint value;
            
            if (bin % 2 == 0) // Even bin: bits 0-7 from current byte, bits 8-11 from next byte's lower nibble
            {
                value = (uint)data[channel, offset];
                offset++;
                uint nibble = (uint)(data[channel, offset] & 0x0F);
                value |= nibble << 8;
            }
            else // Odd bin: bits 0-3 from current byte's upper nibble, bits 4-11 from next byte
            {
                uint nibble = (uint)((data[channel, offset] & 0xF0) >> 4);
                offset++;
                value = nibble | ((uint)data[channel, offset] << 4);
                offset++;
            }
            
            return value;
        }

        /// <summary>
        /// Inverse FFT to convert frequency domain to time domain
        /// Uses simplified approach - you can replace with MathNet.Numerics for better performance
        /// </summary>
        private void InverseFFTCalculation(float[,] fMag, float[,] fAngle, float[,] dataPoints)
        {
            for (int ch = 0; ch < 8; ch++)
            {
                Complex[] frequencyData = new Complex[256];

                // Build complex frequency domain data
                for (int bin = 0; bin < 128; bin++)
                {
                    if (bin == 0)
                    {
                        // DC component (real only)
                        double real = fMag[ch, bin] * Math.Cos(Math.PI * fAngle[ch, bin] / 180.0) * 256.0;
                        frequencyData[bin] = new Complex(real, 0);
                        frequencyData[bin + 128] = Complex.Zero;
                    }
                    else
                    {
                        // AC components (conjugate symmetry for real signal)
                        double angleRad = Math.PI * (fAngle[ch, bin] - 90.0) / 180.0;
                        double scale = fMag[ch, bin] * 128.0 * Math.Sqrt(2.0);
                        
                        Complex c = new Complex(
                            scale * Math.Cos(angleRad),
                            scale * Math.Sin(angleRad)
                        );
                        
                        frequencyData[bin] = c;
                        frequencyData[256 - bin] = Complex.Conjugate(c);
                    }
                }

                // Perform IFFT (simple DFT implementation - replace with MathNet for speed)
                Complex[] timeDomain = SimpleIFFT(frequencyData);

                // Extract real parts
                for (int i = 0; i < 256; i++)
                {
                    dataPoints[ch, i] = (float)timeDomain[i].Real;
                }
            }
        }

        /// <summary>
        /// Simple IFFT implementation using DFT
        /// For production, replace with MathNet.Numerics.IntegralTransforms.Fourier
        /// </summary>
        private Complex[] SimpleIFFT(Complex[] input)
        {
            int N = input.Length;
            Complex[] output = new Complex[N];

            for (int n = 0; n < N; n++)
            {
                Complex sum = Complex.Zero;
                for (int k = 0; k < N; k++)
                {
                    double angle = 2.0 * Math.PI * k * n / N;
                    Complex expTerm = new Complex(Math.Cos(angle), Math.Sin(angle));
                    sum += input[k] * expTerm;
                }
                output[n] = sum / N;
            }

            return output;
        }

        private void ExtractHeader()
        {
            // Extract basic header info (you can expand this)
            // Bytes 0-1: File ID
            // Bytes 2-3: Wave ID
            // Bytes 10: Compression flag
            // Bytes 13-18: MAC address
        }

        private void ParseDateTimeFromFilename(string fileName)
        {
            // Expected format: Waveform_XXX_YYYYMMDD_HHMMSS_UUUUUU_XX
            try
            {
                var parts = fileName.Split('_');
                if (parts.Length >= 5)
                {
                    string datePart = parts[2]; // YYYYMMDD
                    string timePart = parts[3]; // HHMMSS
                    string microPart = parts[4]; // UUUUUU

                    int year = int.Parse(datePart.Substring(0, 4));
                    int month = int.Parse(datePart.Substring(4, 2));
                    int day = int.Parse(datePart.Substring(6, 2));
                    int hour = int.Parse(timePart.Substring(0, 2));
                    int minute = int.Parse(timePart.Substring(2, 2));
                    int second = int.Parse(timePart.Substring(4, 2));

                    CaptureTime = new DateTime(year, month, day, hour, minute, second);
                    MicrosecondOfCapture = int.Parse(microPart);
                }
            }
            catch (Exception ex)
            {
                throw new FormatException($"Invalid waveform filename format: {fileName}", ex);
            }
        }
    }

    /// <summary>
    /// Container for waveform segment data
    /// </summary>
    public class WaveformSegment
    {
        public int SegmentNumber { get; set; }
        public int TotalSegments { get; set; }
        public int SegmentCycles { get; set; }
        public int TotalWaveformCycles { get; set; }
        
        public DateTime CaptureTime { get; set; }
        public int MicrosecondOfCapture { get; set; }
        
        public uint[] SecondOfCycle { get; set; }
        public uint[] MicrosecondOfCycle { get; set; }
        public float[] FrequencyOfCycle { get; set; }
        
        public double[] DeltaTMilliseconds { get; set; }
        public double[,] WaveformValues { get; set; } // [channel, sample]
        
        public double MaxVoltage { get; set; }
        public double MaxCurrent { get; set; }
    }
}
