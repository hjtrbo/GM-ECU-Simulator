using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using LoggingInterface;

namespace UtilityFileInterpreter
{
    public class Interpreter
    {
        public Interpreter(VehicleCommServer.IVehicleCommServerInterface vehicleInterface, 
            ref List<string> calibrationFileNames, string ecuName, List<string> vit2Data, Logger logger,
            string deviceName,string channelName)
        {
            m_interpreterInstructions = new List<InterpreterInstruction>();
            m_routines = new List<Routine>();
            m_calibrationModules = new List<List<byte>>();
            m_header = new Header();
            m_calibrationFileNames = calibrationFileNames;
            m_logger = logger;
            m_ecuName = ecuName;
            m_opCodeHandler = new OpCodeHandler(ref m_header, vehicleInterface, 
                ref m_interpreterInstructions, ref m_routines, ref m_calibrationModules, ecuName, vit2Data, 
                ref m_logger, deviceName, channelName);
        }
        public Interpreter(VehicleCommServer.IVehicleCommServerInterface vehicleInterface,
    ref List<string> calibrationFileNames, string deviceName, string channelName)
        {
            m_interpreterInstructions = new List<InterpreterInstruction>();
            m_routines = new List<Routine>();
            m_calibrationModules = new List<List<byte>>();
            m_header = new Header();
            m_calibrationFileNames = calibrationFileNames;
            m_logger = null;
            m_opCodeHandler = new OpCodeHandler(ref m_header, vehicleInterface,
                ref m_interpreterInstructions, ref m_routines, ref m_calibrationModules, deviceName, channelName);
        }
        public Logger m_logger;
        public string m_utilityFileName;
        public int m_headerOffset;
        public UInt16 m_routineSectionEnd;
        public Header m_header;
        public APIHeader m_apiHeader;
        public OpCodeHandler m_opCodeHandler;
        public List<InterpreterInstruction> m_interpreterInstructions;
        public List<Routine> m_routines;
        public List<List<byte>> m_calibrationModules;
        public List<string> m_calibrationFileNames;
        public string m_ecuName;

        public struct Header
        {
            public UInt16 checksum;
            public UInt16 moduleID;
            public UInt32 partNo; //utility file part number
            public UInt16 designLevel;
            public UInt16 headerType; //0x0000 or offset to 2nd section
            public InterpreterType interpType; //indicates comm protocol
            public UInt16 routineSectionOffset;
            public UInt16 addType; //2,3,or 4 (4 for GMLAN)
            public UInt32 dataAddressInfo;
            public UInt16 dataBytesPerMessage;
            //data bytes per message can be changed by op codes
            public UInt16 effectiveDataBytesPerMessage;
        }
        public struct APIHeader
        {
            public UInt32 formatType;
            public byte[] partNo; //36 byte part no in ascii
            public UInt32 blockNo;
            public UInt32 noOfBlocks;
            public byte[] dataCreationDate; //YYYYMMDDHHMMSS size of 14
            public byte dataType; // 0 norm, 1 gmlan header section - not to be programmed into memory, > 1 error
            public byte[] spare; // spare fields size of 21 bytes
            public UInt32 noOfAddressBytes; //0 - no address section, 0x02 16 bit address, 0x04 32 bit address
            public UInt32 noOfDataBytes;
            public UInt32 crcType;  //0 no crc 1 whatevs dont care
            public UInt32 noOfCRCBytes;
        }

        public struct InterpreterInstruction
        {
            public byte step; 
            public byte opCode; 
            public byte[] actionFields;
            public byte [] gotoFields;
        }
        public struct Routine
        {
            public UInt32 address; //destination address
            public UInt16 length; //length of data
            public byte[] data;
        }
        public enum InterpreterType
        {
            UART = 0x00,
            CLASS_2,
            KW2000,
            GMLAN
        }
        public InterpreterType UintToInterpType(UInt16 input)
        {
            switch (input)
            {
                case 0x00 : 
                    return InterpreterType.UART;
                case 0x01 : 
                    return InterpreterType.CLASS_2;
                case 0x02 : 
                    return InterpreterType.KW2000;
                case 0x03 : 
                    return InterpreterType.GMLAN;
                default : 
                    return InterpreterType.GMLAN;
            }
        }
        public bool openUtilityFile(string fullFileName)
        {
            bool status = false;
            try
            {
                if (File.Exists(fullFileName))
                {
                    using (BinaryReader b = new BinaryReader(File.Open(fullFileName, FileMode.Open)))
                    {
                        m_apiHeader = new APIHeader();
                        ParseAPIHeader(b);

                        //calculate end of routine section ((start of data) + total data bytes)
                        m_routineSectionEnd = (UInt16)((UInt16)m_headerOffset + (UInt16)m_apiHeader.noOfDataBytes);
                        b.BaseStream.Seek(m_headerOffset, SeekOrigin.Begin);
                        ParseHeader(b);
                        ParseInterpreterInstructions(b);
                        b.BaseStream.Seek(m_headerOffset + m_header.routineSectionOffset, SeekOrigin.Begin);
                        ParseRoutines(b);

                    }
                    m_calibrationModules.Clear();
                    foreach (string fileName in m_calibrationFileNames)
                    {
                        m_logger.Log("Trying to Open " + fileName + ".");
                        if (File.Exists(fileName))
                        {
                            using (BinaryReader b = new BinaryReader(File.Open(fileName, FileMode.Open)))
                            {
                                List<byte> calModule = new List<byte>();
                                byte[] bytes;
                                //format type
                                bytes = b.ReadBytes(4);
                                Array.Reverse(bytes);
                                UInt32 formatType = BitConverter.ToUInt32(bytes, 0);
                                m_logger.Log("Reading " + fileName + " as a format " + formatType + "(" + BitConverter.ToString(bytes) + ") file.");
                                if (formatType < 3)
                                {
                                    //Get 'Number of Data Bytes'
                                    b.BaseStream.Seek(0x58, SeekOrigin.Begin);
                                    bytes = b.ReadBytes(4);
                                    Array.Reverse(bytes);
                                    UInt32 calFileDataLength = BitConverter.ToUInt32(bytes, 0);
                                    b.BaseStream.Seek(0x68, SeekOrigin.Begin);
                                    calModule = b.ReadBytes((int)calFileDataLength).ToList();
                                    m_calibrationModules.Add(calModule);
                                }
                                else if (formatType == 3)
                                {
                                    m_logger.Log("Trying to read Data Section Length. ");
                                    //Get 'Length of Data Section'
                                    b.BaseStream.Seek(0x4c, SeekOrigin.Begin);
                                    bytes = b.ReadBytes(4);
                                    Array.Reverse(bytes);
                                    UInt32 calFileDataLength = BitConverter.ToUInt32(bytes, 0);
                                    m_logger.Log("Data Section Length: " + calFileDataLength);
                                    //Get 'Number Of Data Regions', Used to calculate offset for Data Section. 
                                    m_logger.Log("Trying to read Number of DataSections. ");
                                    b.BaseStream.Seek(0x56, SeekOrigin.Begin);
                                    bytes = b.ReadBytes(2);
                                    Array.Reverse(bytes);
                                    UInt16 numDataRegions = BitConverter.ToUInt16(bytes, 0);
                                    m_logger.Log("DataSections: " + numDataRegions);
                                    int dataOffset = 88 + (8 * numDataRegions) + 8;
                                    m_logger.Log("Data Offset Calculated: " + dataOffset);
                                    //Use the offset to get the right amount of data. 
                                    b.BaseStream.Seek(dataOffset, SeekOrigin.Begin);
                                    calModule = b.ReadBytes((int)calFileDataLength).ToList();
                                    m_calibrationModules.Add(calModule);
                                }
                            }
                        }
                        else
                        {
                            if (m_logger != null)
                            {
                                m_logger.Log("ERROR: File does not Exist! ( " + fileName + " )");
                            }
                        }
                    }
                }
                else
                {
                    if (m_logger != null)
                    {
                        m_logger.Log("ERROR: Utility File does not Exist! ( " + fullFileName + " )");
                    }
                }
            }
            catch(Exception e){
                m_logger.Log("Well, this is awkward.");
            }
            return status;
        }

        public void ParseAPIHeader(BinaryReader b)
        {
         /* public byte[] spare; // spare fields size of 21 bytes
            public UInt16 noOfAddressBytes; //0 - no address section, 0x02 16 bit address, 0x04 32 bit address
            public UInt16 noOfDataBytes;
            public UInt16 crcType;  //0 no crc 1 whatevs dont care
            public UInt16 noOfCRCBytes;*/
            byte[] bytes;
            //format type
            bytes = b.ReadBytes(4);
            Array.Reverse(bytes);            
            m_apiHeader.formatType = BitConverter.ToUInt32(bytes, 0);
            //part no
            m_apiHeader.partNo = b.ReadBytes(36);
            //block no
            bytes = b.ReadBytes(4);
            Array.Reverse(bytes);
            m_apiHeader.blockNo = BitConverter.ToUInt32(bytes, 0);
            //num of blocks
            bytes = b.ReadBytes(4);
            Array.Reverse(bytes);
            m_apiHeader.noOfBlocks = BitConverter.ToUInt32(bytes, 0);
            //creation date
            m_apiHeader.dataCreationDate = b.ReadBytes(14);
            //data type
            m_apiHeader.dataType = b.ReadByte();
            //creation date
            if (m_apiHeader.formatType <= 2)
            {
                m_apiHeader.spare = b.ReadBytes(21);
                //num of address bytes
                bytes = b.ReadBytes(4);
                Array.Reverse(bytes);
                m_apiHeader.noOfAddressBytes = BitConverter.ToUInt32(bytes, 0);
                //num of data bytes
                bytes = b.ReadBytes(4);
                Array.Reverse(bytes);
                m_apiHeader.noOfDataBytes = BitConverter.ToUInt32(bytes, 0);
                //crc type
                bytes = b.ReadBytes(4);
                Array.Reverse(bytes);
                m_apiHeader.crcType = BitConverter.ToUInt32(bytes, 0);
                //num of crc bytes
                bytes = b.ReadBytes(4);
                Array.Reverse(bytes);
                m_apiHeader.noOfCRCBytes = BitConverter.ToUInt32(bytes, 0);
                m_headerOffset = 0x64;
            }
            else
            {
                m_logger.Log("PTI File With Format: " + m_apiHeader.formatType);
                m_apiHeader.spare = b.ReadBytes(13);
                //num of address bytes, Not in current #3 Format API?
                //bytes = b.ReadBytes(4);
                //Array.Reverse(bytes);
                m_apiHeader.noOfAddressBytes = 0; // BitConverter.ToUInt32(bytes, 0);
                //num of data bytes
                bytes = b.ReadBytes(4);
                Array.Reverse(bytes);
                m_apiHeader.noOfDataBytes = BitConverter.ToUInt32(bytes, 0);

                //skipping next 3 fields
                b.ReadBytes(6);
                //get #data Regions
                bytes = b.ReadBytes(2);
                Array.Reverse(bytes);
                UInt16 dataRegions = BitConverter.ToUInt16(bytes, 0);
                if(dataRegions >0)
                    b.ReadBytes(8*dataRegions);
                //crc type
                bytes = b.ReadBytes(4);
                Array.Reverse(bytes);
                m_apiHeader.crcType = BitConverter.ToUInt32(bytes, 0);
                //num of crc bytes
                bytes = b.ReadBytes(4);
                Array.Reverse(bytes);
                m_apiHeader.noOfCRCBytes = BitConverter.ToUInt32(bytes, 0);
                m_headerOffset = 96 + (8 * dataRegions);
            }
        }

        public void ParseHeader(BinaryReader b)
        {
            byte[] bytes;
            bytes = b.ReadBytes(2);
            Array.Reverse(bytes);
            m_header.checksum = BitConverter.ToUInt16(bytes,0);
            bytes = b.ReadBytes(2);
            Array.Reverse(bytes);
            m_header.moduleID = BitConverter.ToUInt16(bytes, 0);
            bytes = b.ReadBytes(4);
            Array.Reverse(bytes);
            m_header.partNo = BitConverter.ToUInt32(bytes, 0); //utility file part number
            bytes = b.ReadBytes(2);
            Array.Reverse(bytes);
            m_header.designLevel = BitConverter.ToUInt16(bytes, 0);
            bytes = b.ReadBytes(2);
            Array.Reverse(bytes);
            m_header.headerType = BitConverter.ToUInt16(bytes, 0); //0x0000 or offset to 2nd section
            bytes = b.ReadBytes(2);
            Array.Reverse(bytes);
            m_header.interpType = UintToInterpType(BitConverter.ToUInt16(bytes, 0)); //indicates comm protocol
            bytes = b.ReadBytes(2);
            Array.Reverse(bytes);
            m_header.routineSectionOffset = BitConverter.ToUInt16(bytes, 0);
            bytes = b.ReadBytes(2);
            Array.Reverse(bytes);
            m_header.addType = BitConverter.ToUInt16(bytes, 0); //2,3,or 4 (4 for GMLAN)
            bytes = b.ReadBytes(4);
            Array.Reverse(bytes);
            m_header.dataAddressInfo = BitConverter.ToUInt32(bytes, 0);
            bytes = b.ReadBytes(2);
            Array.Reverse(bytes);
            m_header.dataBytesPerMessage = BitConverter.ToUInt16(bytes, 0);
            m_header.effectiveDataBytesPerMessage = m_header.dataBytesPerMessage;
        }
        public void ParseInterpreterInstructions(BinaryReader b)
        {
            m_interpreterInstructions.Clear();
            while (b.BaseStream.Position < (m_headerOffset + m_header.routineSectionOffset))
            {
                InterpreterInstruction inst = new InterpreterInstruction();
                inst.step = b.ReadByte();
                inst.opCode = b.ReadByte();
                inst.actionFields = b.ReadBytes(4);
                inst.gotoFields = b.ReadBytes(10);
                m_interpreterInstructions.Add(inst);
            }
        }
        //Note stop position unknown - utility file does not follow correct format
        public void ParseRoutines(BinaryReader b)
        {
            try
            {
                byte[] bytes;
                m_routines.Clear();
                while (b.BaseStream.Position < m_routineSectionEnd)
                {
                    Routine routine = new Routine();
                    bytes = b.ReadBytes(4);
                    Array.Reverse(bytes);
                    routine.address = BitConverter.ToUInt32(bytes, 0);
                    bytes = b.ReadBytes(2);
                    Array.Reverse(bytes);
                    routine.length = BitConverter.ToUInt16(bytes, 0);
                    routine.data = b.ReadBytes(routine.length);
                    m_routines.Add(routine);
                }
            }
            catch
            {
                if (m_logger != null)
                {
                    m_logger.Log("ERROR:  "+ m_ecuName +"::Interpreter:  Routine Section Error - Invalid length");
                }
            }
        }
        public void SetRequestResponseIDPair(uint requestID, uint responseID)
        {
            if (m_opCodeHandler != null)
            {
                m_opCodeHandler.m_moduleRequestID = requestID;
                m_opCodeHandler.m_moduleResponseID = responseID;
            }
        }
        public int GetDataSize()
        {
            int dataSize = 0;          
            foreach (Routine routine in m_routines)
            {
                dataSize+= routine.data.Count();
            }
            foreach (List<byte> cal in m_calibrationModules)
            {
                dataSize += cal.Count();
            }
            return dataSize;
        }
    }
}
