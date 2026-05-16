using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VehicleCommServer;
using J2534ChannelLibrary;
using System.Threading;
using LoggingInterface;

namespace UtilityFileInterpreter
{
    public class OpCodeHandler
    {
        public OpCodeHandler(ref Interpreter.Header header, IVehicleCommServerInterface vehicleInterface, 
            ref List<Interpreter.InterpreterInstruction> instructions,
            ref List<Interpreter.Routine> routines, ref List<List<byte>> calModules, string ecuName, List<string> vit2Data, 
            ref Logger logger, string deviceName, string channelName)
        {
            m_header = header;
            m_savedBytes = new List<byte[]>(); 
            m_savedBytesTwoByte = new List<byte[]>();
            m_vit2Data = new List<string>(vit2Data);
            m_opCodeResults = new List<OpCodeResult>();
            m_stopProgrammingSession = false;
            m_counters = new byte[20];
            m_vehicleCommInterface = vehicleInterface;
            m_instructions = instructions;
            m_deviceName = deviceName;
            m_channelName = channelName;
            m_routines = routines;
            m_calibrationModules = calModules;
            m_logger = logger;
            m_ecuName = ecuName;
            m_bytesTransmitted = 0;
            for (int x = 0; x < MAX_NUM_BUFFERS; x++)
            {
                m_savedBytes.Add(new byte[BYTE_STORAGE_MAX]);
                m_savedBytesTwoByte.Add(new byte[2]);
            }
        }
        public OpCodeHandler(ref Interpreter.Header header, IVehicleCommServerInterface vehicleInterface,
    ref List<Interpreter.InterpreterInstruction> instructions,
    ref List<Interpreter.Routine> routines, ref List<List<byte>> calModules, string deviceName, string channelName)
        {
            m_header = header;
            m_savedBytes = new List<byte[]>();
            m_savedBytesTwoByte = new List<byte[]>();
            m_vit2Data = new List<string>();
            m_opCodeResults = new List<OpCodeResult>();
            m_stopProgrammingSession = false;
            m_counters = new byte[20];
            m_vehicleCommInterface = vehicleInterface;
            m_instructions = instructions;
            m_deviceName = deviceName;
            m_channelName = channelName;
            m_routines = routines;
            m_calibrationModules = calModules;
            m_logger = null;
            m_bytesTransmitted = 0;
            for (int x = 0; x < MAX_NUM_BUFFERS; x++)
            {
                m_savedBytes.Add(new byte[BYTE_STORAGE_MAX]);
                m_savedBytesTwoByte.Add(new byte[2]);
            }
        }
        /*Vit2 data should be stored as string public void PopulateVIT2Data(ref List<string> vit2Data)
        {
            System.Text.ASCIIEncoding encoding = new System.Text.ASCIIEncoding();
            foreach (string vd in vit2Data)
            {//convert string into byte array
                addVIT2Data(encoding.GetBytes(vd));
            }
        }*/


        public string m_ecuName;
        public Logger m_logger;
        public const int BYTE_STORAGE_MAX = 256;
        public const int MAX_NUM_BUFFERS = 20;
        public const int TESTER_PRESENT_DELAY = 500;
        public Interpreter.Header m_header;
        public List<byte[]> m_savedBytes;
        public List<byte[]> m_savedBytesTwoByte;
        public List<string> m_vit2Data;
        public byte[] m_counters;
        public UInt32 m_globalMemoryAddress;
        public UInt32 m_globalMemoryLength;
        public UInt32 m_globalHeaderLength;
        public OpCodeResult m_currentOpCodeResult;
        public List<OpCodeResult> m_opCodeResults;
        public bool m_stopProgrammingSession;
        private IVehicleCommServerInterface m_vehicleCommInterface;
        private List<Interpreter.InterpreterInstruction> m_instructions;
        private List<Interpreter.Routine> m_routines;
        public byte m_globalTargetAddress;
        public byte m_globalSourceAddress;
        public uint m_moduleRequestID;
        public uint m_moduleResponseID;
        public string m_deviceName;
        public string m_channelName;
        public List<List<byte>> m_calibrationModules;
        public volatile int m_bytesTransmitted;

        public void addSavedBytes(byte[] bArray)
        {
            m_savedBytes.Add(bArray);
        }
        //clear all saved byte data
        public void clearSavedBytes()
        {
            m_savedBytes.Clear();
        }
        public void addSavedBytesTwoByte(byte[] bArray)
        {
            m_savedBytesTwoByte.Add(bArray);
        }
        //clear all saved byte data
        public void clearSavedBytesTwoByte()
        {
            m_savedBytesTwoByte.Clear();
        }
        public void addVIT2Data(string strData)
        {
            m_vit2Data.Add(strData);
        }
        //clear all VIT2 data
        public void clearVIT2Data()
        {
            m_vit2Data.Clear();
        }

        public class OpCodeResult
        {
            public OpCodeResult(int step,byte opcode, bool result)
            {
                m_step = step;
                m_opCode = opcode;
                m_result = result;
                m_promptOperator = false;
                m_prompt = "";
            }
            public int m_step;
            public byte m_opCode;
            //true pass - false fail
            public bool m_result;
            public bool m_promptOperator;
            public string m_prompt;
        }

        public string GetOpCodeResultString(OpCodeResult opCodeResult)
        {
            string pf;
            if (opCodeResult.m_result)
            {
                pf = "Pass";
            }
            else
            {
                pf = "Fail";
            }
            string result = "SPS" + opCodeResult.m_step.ToString() +
                Convert.ToString(opCodeResult.m_opCode, 16) +
                pf;
            return result;
        }

        public enum UARTOpCodes
        {
            //Common op codes
            COMPARE_BYTES = 0x50,
            COMPARE_CHECKSUM = 0x51,
            COMPARE_DATA = 0x53,
            CHANGE_DATA = 0x54,
            END_WITH_ERROR = 0xEE,
            SET_GLOBAL_MEMORY_ADDRESS = 0xF1,
            SET_GLOBAL_MEMORY_LENGTH = 0xF2,
            SET_GLOBAL_HEADER_LENGTH = 0xF3,
            IGNORE_RESPONSES_FOR_MILLISECONDS = 0xF4,
            OVERRIDE_THE_UTILITY_FILE_MESSAGE_LENGTH_FIELD = 0xF5,
            NO_OPERATION_OP_CODE = 0xF7,
            GOTO_FIELD_CONTINUATION = 0xF8,
            SET_AND_DECREMENT_COUNTER = 0xFB,
            DELAY_FOR_XX_SECONDS = 0xFC,
            RESET_COUNTER = 0xFD,
            END_WITH_SUCCESS = 0xFF,

            //To Do add UART op codes
        }
        public enum Class2OpCodes
        {
            //Common op codes
            COMPARE_BYTES = 0x50,
            COMPARE_CHECKSUM = 0x51,
            COMPARE_DATA = 0x53,
            CHANGE_DATA = 0x54,
            END_WITH_ERROR = 0xEE,
            SET_GLOBAL_MEMORY_ADDRESS = 0xF1,
            SET_GLOBAL_MEMORY_LENGTH = 0xF2,
            SET_GLOBAL_HEADER_LENGTH = 0xF3,
            IGNORE_RESPONSES_FOR_MILLISECONDS = 0xF4,
            OVERRIDE_THE_UTILITY_FILE_MESSAGE_LENGTH_FIELD = 0xF5,
            NO_OPERATION_OP_CODE = 0xF7,
            GOTO_FIELD_CONTINUATION = 0xF8,
            SET_AND_DECREMENT_COUNTER = 0xFB,
            DELAY_FOR_XX_SECONDS = 0xFC,
            RESET_COUNTER = 0xFD,
            END_WITH_SUCCESS = 0xFF,

            //To do add Class 2 op codes
        }
        public enum KWP2000OpCodes
        {
            //Common op codes
            COMPARE_BYTES = 0x50,
            COMPARE_CHECKSUM = 0x51,
            COMPARE_DATA = 0x53,
            CHANGE_DATA = 0x54,
            END_WITH_ERROR = 0xEE,
            SET_GLOBAL_MEMORY_ADDRESS = 0xF1,
            SET_GLOBAL_MEMORY_LENGTH = 0xF2,
            SET_GLOBAL_HEADER_LENGTH = 0xF3,
            IGNORE_RESPONSES_FOR_MILLISECONDS = 0xF4,
            OVERRIDE_THE_UTILITY_FILE_MESSAGE_LENGTH_FIELD = 0xF5,
            NO_OPERATION_OP_CODE = 0xF7,
            GOTO_FIELD_CONTINUATION = 0xF8,
            SET_AND_DECREMENT_COUNTER = 0xFB,
            DELAY_FOR_XX_SECONDS = 0xFC,
            RESET_COUNTER = 0xFD,
            END_WITH_SUCCESS = 0xFF,

            //to do add KWP2000 op codes
        }
        public enum GMLANOpCodes : byte
        {
            //Common op codes
            COMPARE_BYTES = 0x50,
            COMPARE_CHECKSUM = 0x51,
            COMPARE_DATA = 0x53,
            CHANGE_DATA = 0x54,
            EVALUATE_RPO = 0x55, //Not implemented - not needed 
            INTERPRETER_IDENTIFIER = 0x56,
            END_WITH_ERROR = 0xEE,
            SET_GLOBAL_MEMORY_ADDRESS = 0xF1,
            SET_GLOBAL_MEMORY_LENGTH = 0xF2,
            SET_GLOBAL_HEADER_LENGTH = 0xF3,
            IGNORE_RESPONSES_FOR_MILLISECONDS = 0xF4,
            OVERRIDE_THE_UTILITY_FILE_MESSAGE_LENGTH_FIELD = 0xF5,
            NO_OPERATION_OP_CODE = 0xF7,
            GOTO_FIELD_CONTINUATION = 0xF8,
            SET_AND_DECREMENT_COUNTER = 0xFB,
            DELAY_FOR_XX_SECONDS = 0xFC,
            RESET_COUNTER = 0xFD,
            END_WITH_SUCCESS = 0xFF,

            //GMLAN Specific op codes
            SETUP_GLOBAL_VARIABLES = 0x01,
            SEND_SINGLE_FRAME = 0x02, // development only - not implemented
            REINIT_NETWORK_FOR_PROGRAMMING = 0x03, // development only - not implemented
            INITIATE_DIAGNOSTIC_OPERATION = 0x10,
            CLEAR_DTCS = 0x14,
            READ_DATA_BY_IDENTIFIER = 0x1A,
            SECURITY_ACCESS = 0x27,
            REQUEST_DOWNLOAD = 0x34,
            WRITE_DATA_BY_IDENTIFIER = 0x3B,
            SET_COMMUNICATIONS_PARAMETERS = 0x84,
            REPORT_PROGRAMMED_STATE_AND_SAVE_RESPONSE = 0xA2,
            READ_DATA_BY_PACKET_IDENTIFIER = 0xAA,
            REQUEST_DEVICE_CONTROL = 0xAE,
            BLOCK_TRANSFER_TO_RAM = 0xB0,
            RETURN_TO_NORMAL_MODE = 0x20,// development only - not implemented
            READ_DATA_BY_PARAMETER_IDENTIFIER = 0x22,
            SECURITY_CODE = 0x25 //development only - not implemented
        }

        public enum DCUModuleOpCodes
        {
            


        }

        public enum MimamoriOpCodes
        {
            //Common op codes
            COMPARE_BYTES = 0x50,
            COMPARE_CHECKSUM = 0x51,
            COMPARE_DATA = 0x53,
            CHANGE_DATA = 0x54,
            END_WITH_ERROR = 0xEE,
            SET_GLOBAL_MEMORY_ADDRESS = 0xF1,
            SET_GLOBAL_MEMORY_LENGTH = 0xF2,
            SET_GLOBAL_HEADER_LENGTH = 0xF3,
            IGNORE_RESPONSES_FOR_MILLISECONDS = 0xF4,
            OVERRIDE_THE_UTILITY_FILE_MESSAGE_LENGTH_FIELD = 0xF5,
            NO_OPERATION_OP_CODE = 0xF7,
            GOTO_FIELD_CONTINUATION = 0xF8,
            SET_AND_DECREMENT_COUNTER = 0xFB,
            DELAY_FOR_XX_SECONDS = 0xFC,
            RESET_COUNTER = 0xFD,
            END_WITH_SUCCESS = 0xFF,

            //Mimamori Specific Op Codes
            ECM = 0x20,
            SCR = 0x22,
            TURBO_CONTROLLER = 0x23,
            TCM = 0x24,
            ABS = 0x29,
            VAT = 0x2A,
            AIRSUS = 0x2B,
            IDLING_STOP_START = 0x2C,
            HYBRID_SYSTEM = 0x30,
            TPMS = 0x32,
            ECU = 0x34,
            HSA = 0x35,
            BLS = 0x36,
            SRS = 0x37,
            SAS = 0x38,
            DOOR_ECU = 0x39,
            GVW = 0xA2,
        }
        public string OpCodeToString(byte opCode)
        {
            switch (opCode)
            {
                //Common Op Codes
                case (byte)GMLANOpCodes.COMPARE_BYTES:
                    return "COMPARE_BYTES";
                case (byte)GMLANOpCodes.COMPARE_CHECKSUM:
                    return "COMPARE_CHECKSUM";
                case (byte)GMLANOpCodes.COMPARE_DATA:
                    return "COMPARE_DATA";
                case (byte)GMLANOpCodes.CHANGE_DATA:
                    return "CHANGE_DATA";
                //case (byte)GMLANOpCodes.EVALUATE_RPO:
                //    break;
                case (byte)GMLANOpCodes.INTERPRETER_IDENTIFIER:
                    return "INTERPRETER_IDENTIFIER";
                case (byte)GMLANOpCodes.END_WITH_ERROR:
                    return "END_WITH_ERROR";
                case (byte)GMLANOpCodes.SET_GLOBAL_MEMORY_ADDRESS:
                    return "SET_GLOBAL_MEMORY_ADDRESS";
                case (byte)GMLANOpCodes.SET_GLOBAL_MEMORY_LENGTH:
                    return "SET_GLOBAL_MEMORY_LENGTH";
                case (byte)GMLANOpCodes.SET_GLOBAL_HEADER_LENGTH:
                    return "SET_GLOBAL_HEADER_LENGTH";
                //case (byte)GMLANOpCodes.IGNORE_RESPONSES_FOR_MILLISECONDS:
                //return IgnoreResponsesForMilliseconds(instruction);
                case (byte)GMLANOpCodes.OVERRIDE_THE_UTILITY_FILE_MESSAGE_LENGTH_FIELD:
                    return "OVERRIDE_THE_UTILITY_FILE_MESSAGE_LENGTH_FIELD";
                case (byte)GMLANOpCodes.NO_OPERATION_OP_CODE:
                    return "NO_OPERATION_OP_CODE";
                case (byte)GMLANOpCodes.GOTO_FIELD_CONTINUATION:
                    return "GOTO_FIELD_CONTINUATION";
                case (byte)GMLANOpCodes.SET_AND_DECREMENT_COUNTER:
                    return "SET_AND_DECREMENT_COUNTER";
                case (byte)GMLANOpCodes.DELAY_FOR_XX_SECONDS:
                    return "DELAY_FOR_XX_SECONDS";
                case (byte)GMLANOpCodes.RESET_COUNTER:
                    return "RESET_COUNTER";
                case (byte)GMLANOpCodes.END_WITH_SUCCESS:
                    return "END_WITH_SUCCESS";

                //GMLAN Op Codes
                case (byte)GMLANOpCodes.SETUP_GLOBAL_VARIABLES:
                    return "SETUP_GLOBAL_VARIABLES";
                //GM Dev only
                //case (byte)GMLANOpCodes.SEND_SINGLE_FRAME:
                //    break;
                //case (byte)GMLANOpCodes.REINIT_NETWORK_FOR_PROGRAMMING:
                //    break;                 
                case (byte)GMLANOpCodes.INITIATE_DIAGNOSTIC_OPERATION:
                    return "INITIATE_DIAGNOSTIC_OPERATION";
                case (byte)GMLANOpCodes.CLEAR_DTCS:
                    return "CLEAR_DTCS";
                case (byte)GMLANOpCodes.READ_DATA_BY_IDENTIFIER:
                    return "READ_DATA_BY_IDENTIFIER";
                case (byte)GMLANOpCodes.SECURITY_ACCESS:
                    return "SECURITY_ACCESS";
                case (byte)GMLANOpCodes.REQUEST_DOWNLOAD:
                    return "REQUEST_DOWNLOAD";
                case (byte)GMLANOpCodes.WRITE_DATA_BY_IDENTIFIER:
                    return "WRITE_DATA_BY_IDENTIFIER";
                case (byte)GMLANOpCodes.SET_COMMUNICATIONS_PARAMETERS:
                    return "SET_COMMUNICATIONS_PARAMETERS";
                case (byte)GMLANOpCodes.REPORT_PROGRAMMED_STATE_AND_SAVE_RESPONSE:
                    return "REPORT_PROGRAMMED_STATE_AND_SAVE_RESPONSE";
                case (byte)GMLANOpCodes.READ_DATA_BY_PACKET_IDENTIFIER:
                    return "READ_DATA_BY_PACKET_IDENTIFIER";
                case (byte)GMLANOpCodes.REQUEST_DEVICE_CONTROL:
                    return "REQUEST_DEVICE_CONTROL";
                case (byte)GMLANOpCodes.BLOCK_TRANSFER_TO_RAM:
                    return "BLOCK_TRANSFER_TO_RAM";
                //case (byte)GMLANOpCodes.RETURN_TO_NORMAL_MODE:
                //    break;
                case (byte)GMLANOpCodes.READ_DATA_BY_PARAMETER_IDENTIFIER:
                    return "BLOCK_TRANSFER_TO_RAM";
                //case (byte)GMLANOpCodes.SECURITY_CODE:
                //    break;
                default:
                    return "Unsupported";
            }
        }

        public void LogOpCodeDetails(Interpreter.InterpreterInstruction instruction)
        {

            m_logger.Log("INFO:  " + m_ecuName + "::OpCodeHandler: OpCodeDetails Instruction:" + OpCodeToString(instruction.opCode));
            m_logger.Log("INFO:  " + m_ecuName + "::OpCodeHandler: Action Fields:" + Convert.ToString(instruction.actionFields[0], 16) + " " + Convert.ToString(instruction.actionFields[1], 16) + " " +
                Convert.ToString(instruction.actionFields[2], 16) + " " + Convert.ToString(instruction.actionFields[3], 16));
            m_logger.Log("INFO:  " + m_ecuName + "::OpCodeHandler: Goto Fields:" + Convert.ToString(instruction.gotoFields[0], 16) + " " + Convert.ToString(instruction.gotoFields[1], 16) + " " +
                Convert.ToString(instruction.gotoFields[2], 16) + " " + Convert.ToString(instruction.gotoFields[3], 16));
        }
        
        //returns next step
        public byte ProcessGMLANOpCode(Interpreter.InterpreterInstruction instruction)
        {
            m_currentOpCodeResult = new OpCodeResult(instruction.step, instruction.opCode, true);
            m_opCodeResults.Add(m_currentOpCodeResult);
            //log op code details
            if (m_logger != null)
            {
                LogOpCodeDetails(instruction);
            }
            switch (instruction.opCode)
            {
                //Common Op Codes
                case (byte)GMLANOpCodes.COMPARE_BYTES:
                    return CompareBytes(instruction);
                case (byte)GMLANOpCodes.COMPARE_CHECKSUM:
                    return CompareChecksum(instruction);
                case (byte)GMLANOpCodes.COMPARE_DATA:
                    return CompareData(instruction);
                case (byte)GMLANOpCodes.CHANGE_DATA:
                    return ChangeData(instruction);
                //case (byte)GMLANOpCodes.EVALUATE_RPO:
                //    break;
                case (byte)GMLANOpCodes.INTERPRETER_IDENTIFIER:
                    return InterpreterIdentifier(instruction);
                case (byte)GMLANOpCodes.END_WITH_ERROR:
                    return EndWithError(instruction);
                case (byte)GMLANOpCodes.SET_GLOBAL_MEMORY_ADDRESS:
                    return SetGlobalMemoryAddress(instruction);
                case (byte)GMLANOpCodes.SET_GLOBAL_MEMORY_LENGTH:
                    return SetGlobalMemoryLength(instruction);
                case (byte)GMLANOpCodes.SET_GLOBAL_HEADER_LENGTH:
                    return SetGlobalHeaderLength(instruction);
                //case (byte)GMLANOpCodes.IGNORE_RESPONSES_FOR_MILLISECONDS:
                    //return IgnoreResponsesForMilliseconds(instruction);
                case (byte)GMLANOpCodes.OVERRIDE_THE_UTILITY_FILE_MESSAGE_LENGTH_FIELD:
                    return OverrideUtilityFileMessageLengthField(instruction);
                case (byte)GMLANOpCodes.NO_OPERATION_OP_CODE:
                    return NoOpOpCode(instruction);
                case (byte)GMLANOpCodes.GOTO_FIELD_CONTINUATION:
                    return GotoFieldContinuation(instruction);
                case (byte)GMLANOpCodes.SET_AND_DECREMENT_COUNTER:
                    return SetAndDecrementCounter(instruction);
                case (byte)GMLANOpCodes.DELAY_FOR_XX_SECONDS:
                    return DelayForXXSeconds(instruction);
                case (byte)GMLANOpCodes.RESET_COUNTER:
                    return ResetCounter(instruction);
                case (byte)GMLANOpCodes.END_WITH_SUCCESS:
                    return EndWithSuccess(instruction);

                //GMLAN Op Codes
                case (byte)GMLANOpCodes.SETUP_GLOBAL_VARIABLES:
                    return SetupGlobalVariables(instruction);
                    //GM Dev only
                //case (byte)GMLANOpCodes.SEND_SINGLE_FRAME:
                //    break;
                //case (byte)GMLANOpCodes.REINIT_NETWORK_FOR_PROGRAMMING:
                //    break;                 
                case (byte)GMLANOpCodes.INITIATE_DIAGNOSTIC_OPERATION:
                    return InitiateDiagnosticOperation(instruction);
                case (byte)GMLANOpCodes.CLEAR_DTCS:
                    return ClearDTCs(instruction);
                case (byte)GMLANOpCodes.READ_DATA_BY_IDENTIFIER:
                    return ReadDataByIdentifier(instruction);
                case (byte)GMLANOpCodes.SECURITY_ACCESS:
                    return SecurityAccess(instruction);
                case (byte)GMLANOpCodes.REQUEST_DOWNLOAD:
                    return RequestDownload(instruction);
                case (byte)GMLANOpCodes.WRITE_DATA_BY_IDENTIFIER:
                    return WriteDataByIdentifier(instruction);
                case (byte)GMLANOpCodes.SET_COMMUNICATIONS_PARAMETERS:
                    return SetCommunicationsParameters(instruction);
                case (byte)GMLANOpCodes.REPORT_PROGRAMMED_STATE_AND_SAVE_RESPONSE:
                    return ReportProgrammedStateAndSaveResponse(instruction);
                case (byte)GMLANOpCodes.READ_DATA_BY_PACKET_IDENTIFIER:
                    return ReadDataByPacketIdentifier(instruction);
                case (byte)GMLANOpCodes.REQUEST_DEVICE_CONTROL:
                    return RequestDeviceControl(instruction);
                case (byte)GMLANOpCodes.BLOCK_TRANSFER_TO_RAM:
                    return BlockTransferToRAM(instruction);
                //case (byte)GMLANOpCodes.RETURN_TO_NORMAL_MODE:
                //    break;
                case (byte)GMLANOpCodes.READ_DATA_BY_PARAMETER_IDENTIFIER:
                    return ReadDataByParameterIdentifier(instruction);
                //case (byte)GMLANOpCodes.SECURITY_CODE:
                //    break;
                default :
                    return 0xFF;
            }
        }
        //Utility Functions
        public void CopyRoutineToStorageBuffer(byte routineSource, byte storageDestination)
        {
            storedByteAddressCheck(storageDestination);
            if (m_stopProgrammingSession)
            {
                return;
            }
            if (m_savedBytes[storageDestination] == null)
            {
                m_savedBytes[storageDestination] = new byte[BYTE_STORAGE_MAX];
            }
            List<byte> routineData = GetRoutineData(routineSource).ToList();
            if (m_stopProgrammingSession)
            {
                return;
            }
            //copy routine contents to storage buffer
            for (int x = 0; x < routineData.Count; x++)
            {
                m_savedBytes[storageDestination][x] = routineData[x];
            }
        }
        public void CopyVIT2ToStorageBuffer(byte vit2Source, byte storageDestination)
        {
            storedByteAddressCheck(storageDestination);
            if (m_stopProgrammingSession)
            {
                return;
            }
            if (m_savedBytes[storageDestination] == null)
            {
                m_savedBytes[storageDestination] = new byte[BYTE_STORAGE_MAX];
            }
            List<byte> vit2data = new List<byte>(stringToASCIIByteArray(GetVIT2Data(vit2Source)));
            if (m_stopProgrammingSession)
            {
                return;
            }
            //copy vit2 contents then pad buffer with 0s
            for (int x = 0; x < vit2data.Count; x++)
            {
                m_savedBytes[storageDestination][x] = vit2data[x];
            }
            for (int x = vit2data.Count; x < BYTE_STORAGE_MAX; x++)
            {
                m_savedBytes[storageDestination][x] = 0x00;
            }
        }
        //Get locally stored bytes requested by op code
        public void CopyStorageBuffer(byte source, byte destination)
        {
            storedByteAddressCheck(source);
            if (m_stopProgrammingSession)
            {
                return;
            }
            storedByteAddressCheck(destination);
            if (m_stopProgrammingSession)
            {
                return;
            }
            if (m_savedBytes[destination] == null)
            {
                m_savedBytes[destination] = new byte[BYTE_STORAGE_MAX];
            }
            //copy entire contents of buffer
            for (int x = 0; x < BYTE_STORAGE_MAX; x++ )
            {
                m_savedBytes[destination][x] = m_savedBytes[source][x];
            }
        }

        //Get locally stored bytes requested by op code
        public byte[] GetBytesFromID(byte savedByteID)
        {
            storedByteAddressCheck(savedByteID);
            if (m_stopProgrammingSession)
            {//return address 0 - does not matter
                return m_savedBytes[0x00];
            }
            return m_savedBytes[savedByteID];
        }
        //Get locally stored bytes requested by op code
        public byte[] GetBytesFromIDTwoByte(byte savedByteID)
        {
            storedByteAddressCheck(savedByteID);
            if (m_stopProgrammingSession)
            {//return address 0 - does not matter
                return m_savedBytesTwoByte[0x00];
            }
            return m_savedBytesTwoByte[savedByteID];
        }
        //Set locally stored bytes requested by op code
        public void SetBytesFromID(byte savedByteID, byte[] data, int bufferLength)
        {
            storedByteAddressCheck(savedByteID);
            if (bufferLength > BYTE_STORAGE_MAX)
            {
                m_currentOpCodeResult.m_promptOperator = true;
                m_currentOpCodeResult.m_prompt = "FATAL ERROR: Storage Address Invalid";
                m_stopProgrammingSession = true;
            }
            if (!m_stopProgrammingSession)
            {
                if (m_savedBytes[savedByteID] == null)
                {
                    m_savedBytes[savedByteID] = new byte[BYTE_STORAGE_MAX];
                }
                //set saved byte value to data and zero out remaining data
                for (int x = 0; x < bufferLength; x++)
                {
                    m_savedBytes[savedByteID][x] = data[x];
                }
                //zero out remaining data
                for (int x = bufferLength; x < BYTE_STORAGE_MAX; x++)
                {
                    m_savedBytes[savedByteID][x] = 0x00;
                }
            }
        }
        //Set locally stored bytes requested by op code
        public void SetBytesFromIDTwoByte(byte savedByteID, byte[] data, int bufferLength)
        {
            storedByteAddressCheck(savedByteID);
            if (bufferLength > 2)
            {
                if (m_logger != null)
                {
                    m_logger.Log("ERROR:  " + m_ecuName + "::OpCodeHandler: Two Byte Data buffer length specified out of range");
                }
                m_currentOpCodeResult.m_result = false;
                m_currentOpCodeResult.m_promptOperator = true;
                m_currentOpCodeResult.m_prompt = "FATAL ERROR: 2B Storage Length Invalid";
                m_stopProgrammingSession = true;
            }
            if (!m_stopProgrammingSession)
            {
                if (m_savedBytesTwoByte[savedByteID] == null)
                {
                    m_savedBytesTwoByte[savedByteID] = new byte[2];
                }
                //set saved byte value to data and zero out remaining data
                for (int x = 0; x < bufferLength; x++)
                {
                    m_savedBytesTwoByte[savedByteID][x] = data[x];
                }
                //zero out remaining data
                for (int x = bufferLength; x < 2; x++)
                {
                    m_savedBytesTwoByte[savedByteID][x] = 0x00;
                }
            }
        }

//Get locally stored Valid internal data identifier bytes requested by op code
        public string GetVIT2Data(byte vit2ByteID)
        {
            int index = 0;
            if (vit2ByteID > 0x00 && vit2ByteID < 0x14)
            {//this is a software mod pn index = vit2byteID - 1
                index = vit2ByteID - 1;
            }
            else if (vit2ByteID == 0x41)
            {//vin number
                index = m_vit2Data.Count - 1;
            }
            else 
            {
                if (m_logger != null)
                {
                    m_logger.Log("ERROR:  " + m_ecuName + "::OpCodeHandler: GetVIT2Data Index out of range");
                }
                m_currentOpCodeResult.m_result = false;
                m_currentOpCodeResult.m_promptOperator = true;
                m_currentOpCodeResult.m_prompt = "FATAL ERROR: VIT2 Index Invalid";
                m_stopProgrammingSession = true;
                return null;
            }
            return m_vit2Data[index];
        }


//TO DO add error checking
        //Get routine data bytes requested by op code
        public byte[] GetRoutineData(byte routineDataByteID)
        {
            return m_routines[routineDataByteID].data;
        }

        public void storedByteAddressCheck(byte savedByteID)
        {//NOTE: only ids 0 - MAX_NUM_BUFFERS - 1 are valid
            if (savedByteID >= MAX_NUM_BUFFERS)
            {//indicate failure
                if (m_logger != null)
                {
                    m_logger.Log("ERROR:  " + m_ecuName + "::OpCodeHandler: Stored Byte Data Address specified out of range");
                }
                m_currentOpCodeResult.m_result = false;
                m_currentOpCodeResult.m_promptOperator = true;
                m_currentOpCodeResult.m_prompt = "FATAL ERROR : Storage Address Invalid";
                m_stopProgrammingSession = true;
            }
        }

        public bool AreBytesArraysEqual(byte[] data1, byte[] data2)
        {
            if (data1 == data2)
            {
                return true;
            }
            if ((data1 != null) && (data2 != null))
            {
                // SPS manual does not specify how to do comparison data1 considered to be
                // master
                //if (data1.Length != data2.Length)
                //{
                //    return false;
                //}
                if (data2.Length < data1.Length)
                {//protection
                    return false;
                }
                for (int i = 0; i < data1.Length; i++)
                {
                    if (data1[i] != data2[i])
                    {
                        return false;
                    }
                }
                return true;
            }
            return false;
        }


        //Common Op Code Operations
        public byte CompareBytes(Interpreter.InterpreterInstruction instruction)
        {
            byte [] comparisonBytes;
            byte ac1 = instruction.actionFields[1];
            byte ac2 = instruction.actionFields[2];
            byte savedByteID = instruction.actionFields[0];

            comparisonBytes = GetBytesFromIDTwoByte(savedByteID);
            if (!m_stopProgrammingSession)
            {
                if ((ac1 == comparisonBytes[0]) && (ac2 == comparisonBytes[1]))
                {//comparison matches goto G1
                    return instruction.gotoFields[1];
                }
                else
                {//Goto G3
                    return instruction.gotoFields[3];
                }
            }
            else
            {
                return 0xFF;
            }
        }
        public List<byte> GetCalibrationModule(UInt16 id)
        {
            if (id < m_calibrationModules.Count)
            {
                return m_calibrationModules[id];
            }
            else
            {//severe error - id out of range
                if (m_logger != null)
                {
                    m_logger.Log("ERROR:  " + m_ecuName + "::OpCodeHandler: Calibration Module specified out of range");
                }
                m_currentOpCodeResult.m_result = false;
                m_currentOpCodeResult.m_promptOperator = true;
                m_currentOpCodeResult.m_prompt = "FATAL ERROR: Cal ID Invalid";
                m_stopProgrammingSession = true;
                return m_calibrationModules[0];
            }                
        }
        public byte CompareChecksum(Interpreter.InterpreterInstruction instruction)
        {
            UInt16 sum16Bit = 0x00;
            UInt32 sum32Bit = 0x00;
            List<byte> storedBytes = new List<byte>();
            List<byte> comparisonBytes = new List<byte>();
            storedBytes = GetCalibrationModule(instruction.actionFields[1]);
            if (!m_stopProgrammingSession)
            {
                if (instruction.actionFields[2] == 0)
                {//sum up all data bytes of the module specified by AC1 as 16 bit value               
                    foreach (byte b in storedBytes)
                    {
                        sum16Bit += b;
                    }
                }
                else
                {//calculate the crc-32 of the module specified by ac1 as 32 bit value
                    foreach (byte b in storedBytes)
                    {
                        sum32Bit += b;
                    }
                }
                //get data to compare checksum with
                //modified for 2 byte buffers comparisonBytes = GetBytesFromID(instruction.actionFields[0]).ToList();
                if (instruction.actionFields[2] == 0)
                {//compare calculated checksum with 2 byte storage specified by ac0
                    comparisonBytes = GetBytesFromIDTwoByte(instruction.actionFields[0]).ToList();
                    if ((comparisonBytes[1] == (byte)(0x00FF & sum16Bit)) &&
                        (comparisonBytes[0] == (byte)((0xFF00 & sum16Bit) >> 8)))
                    {
                        return instruction.gotoFields[1];
                    }
                    else
                    {
                        return instruction.gotoFields[3];
                    }

                }
                else
                {
                    comparisonBytes = GetBytesFromID(instruction.actionFields[0]).ToList();
                    if (instruction.actionFields[2] == 1)
                    {//complement checksum 
                        sum32Bit = ~sum32Bit;
                    }
                    //compare with first 4 bytes of the 256 byte storage specified by AC0
                    if ((comparisonBytes[3] == (byte)(0x000000FF & sum32Bit)) &&
                        (comparisonBytes[2] == (byte)((0x0000FF00 & sum32Bit) >> 8)) &&
                        (comparisonBytes[1] == (byte)((0x00FF0000 & sum32Bit) >> 16)) &&
                        (comparisonBytes[0] == (byte)((0xFF000000 & sum32Bit) >> 24)))
                    {
                        return instruction.gotoFields[1];
                    }
                    else
                    {
                        return instruction.gotoFields[3];
                    }
                }
            }
            return 0xFF;
        }
        public byte CompareData(Interpreter.InterpreterInstruction instruction)
        {
            List<byte> data2 = new List<byte>();
            List<byte> data1 = new List<byte>();
            data2 = GetBytesFromID(instruction.actionFields[0]).ToList();
            if (!m_stopProgrammingSession)
            {
                if (instruction.actionFields[3] == 0)
                {//set data 1 to internal data (vit2) where AC1 identifies the info
                    if (instruction.actionFields[2] != 0)
                    {
                        data1 = stringToBCD(GetVIT2Data(instruction.actionFields[1])).ToList();
                    }
                    else
                    {// use ascii representation
                        data1 = stringToASCIIByteArray(GetVIT2Data(instruction.actionFields[1])).ToList();
                    }
                }
                else if (instruction.actionFields[3] == 1)
                {// set data 1 to routine data indicated by ac1
                    data1 = GetRoutineData(instruction.actionFields[1]).ToList();
                }
                else if (instruction.actionFields[3] == 2)
                {//set data1 to stored information indicated by ac1
                    data1 = GetBytesFromID(instruction.actionFields[1]).ToList();
                }

                if ((instruction.actionFields[2] != 0) && (instruction.actionFields[3] != 0))
                {//convert data - dont think this will ever occur
                    //01 ascii to 4 byte BCD unsigned number
                    /* ignore - vit2 data all stored as bytes if (instruction.actionFields[2] == 01)
                    {
                        List<byte> temp = new List<byte>();
                        foreach (byte b in data1)
                        {
                            temp.Add(b);
                        }
                        data1.Clear();
                        foreach (byte b in temp)
                        {
                            data1.Add(0x00);
                            data1.Add(0x00);
                            data1.Add(0x00);
                            data1.Add(b);
                        }
                    }*/

                }
                //check to see if arrays are equal
                if (AreBytesArraysEqual(data1.ToArray(), data2.ToArray()))
                {//if all bytes in data1 (max 256) match data 2 G1
                    return instruction.gotoFields[1];
                }
                else //return G3
                {
                    return instruction.gotoFields[3];
                }
            }
            return 0xFF;

        }

        private byte[] stringToBCD(string str)
        {
            ulong num = 0;
            byte[] usnArray = new byte[4]{0x00,0x00,0x00,0x00};
            for (uint i = 0; i < str.Length; i++)
            {
                ulong val = (uint)str[str.Length -1 - (int)i] - 48;
                if (val < 0 || val > 9) 
                    throw new ArgumentOutOfRangeException();
                num += val * (ulong)(Math.Pow((double)10,(double)i));
            }
            //convert to four byte array
            usnArray[0] = (byte)((num & 0xFF000000) >> 24);
            usnArray[1] = (byte)((num & 0x00FF0000) >> 16);
            usnArray[2] = (byte)((num & 0x0000FF00) >> 8);
            usnArray[3] = (byte)(num & 0x000000FF);
            return usnArray;
        }

        public byte[] stringToASCIIByteArray(string strData)
        {
            System.Text.ASCIIEncoding encoding = new System.Text.ASCIIEncoding();
            return encoding.GetBytes(strData);
        }


        public byte ChangeData(Interpreter.InterpreterInstruction instruction)
        {
            byte [] data;
            //byte position or number positions to shift
            byte position = instruction.actionFields[1];
            byte operation = instruction.actionFields[2];
            byte mask = instruction.actionFields[3];
            data = GetBytesFromID(instruction.actionFields[0]);
            if (!m_stopProgrammingSession)
            {
                //perform operation
                if (operation == 0x00)
                {//Equal op
                    data[position] = mask;
                }
                else if (operation == 0x01)
                {//AND op
                    data[position] = (byte)(data[position] & mask);
                }
                else if (operation == 0x02)
                {//OR op
                    data[position] = (byte)(data[position] | mask);
                }
                else if (operation == 0x03)
                {//XOR op
                    data[position] = (byte)(data[position] ^ mask);
                }
                else if (operation == 0x04)
                {//SHL op
                    for (int x = 0; x < data.Length; x++)
                    {//shift data bytes
                        if ((position + x) < data.Length)
                        {//if not beyond array bounds
                            data[x] = data[position + x];
                        }
                        else
                        {//zero fill
                            data[x] = 0x00;
                        }
                    }
                }
                else if (operation == 0x05)
                {//SHR op
                    for (int x = data.Length - 1; x > -1; x--)
                    {//shift data bytes
                        if ((x - position) > -1)
                        {//if not beyond array bounds
                            data[x] = data[x - position];
                        }
                        else
                        {//zero fill
                            data[x] = 0x00;
                        }
                    }
                }
                else if (operation == 0x06)
                {//load routine specified by ac1 into buffer specified by ac0
                    CopyRoutineToStorageBuffer(instruction.actionFields[1], instruction.actionFields[0]);
                    if (m_stopProgrammingSession)
                    {
                        return 0xFF;
                    }
                }
                else if (operation == 0x07)
                {//load vit2 data specified by ac1 into buffer specified by ac0
                    CopyVIT2ToStorageBuffer(instruction.actionFields[1], instruction.actionFields[0]);
                    if (m_stopProgrammingSession)
                    {
                        return 0xFF;
                    }
                }
                else if (operation == 0x08)
                {//copy bytes from Ac1 into Ac0
                    CopyStorageBuffer(instruction.actionFields[1], instruction.actionFields[0]);
                    if (m_stopProgrammingSession)
                    {
                        return 0xFF;
                    }
                }
                return instruction.gotoFields[1];
            }
            return 0xFF;
        }

        public byte InterpreterIdentifier(Interpreter.InterpreterInstruction instruction)
        {//We are Manufacturing interpreter - return G3
            return instruction.gotoFields[3];
        }

        public byte EndWithError(Interpreter.InterpreterInstruction instruction)
        {
            m_currentOpCodeResult.m_promptOperator = true;
            //new manual removes specific prompts
            /*if (instruction.actionFields[0] == 0x00)
            {//can retest no product replacement necessary
                m_currentOpCodeResult.m_prompt = "FAILED : Retest Possible";
            }
            else
            {//instruct replace control module
                m_currentOpCodeResult.m_prompt = "FAILED : Replace Module before retest";
            }*/
            if (m_logger != null)
            {
                m_logger.Log("INFO:  " + m_ecuName + "::OpCodeHandler: End With Error Op-Code Reached");
            }
            m_currentOpCodeResult.m_promptOperator = true;
            m_currentOpCodeResult.m_prompt = "Failure: End With Error Instruction";
            m_currentOpCodeResult.m_result = false;
            m_stopProgrammingSession = true;
            //TO DO what should be returned?
            return 0xFF;
        }
        public byte SetGlobalMemoryAddress(Interpreter.InterpreterInstruction instruction)
        {
            //set global mem address return G1
            m_globalMemoryAddress = 
                ((UInt32)((instruction.actionFields[0] << 16) & 0x00FF0000) |
                (UInt32)((instruction.actionFields[1] << 8) & 0x0000FF00) |
                (UInt32)(instruction.actionFields[2] & 0x000000FF) |
                (UInt32)((instruction.actionFields[3] << 24) & 0xFF000000));
            return instruction.gotoFields[1];
        }
        public byte SetGlobalMemoryLength(Interpreter.InterpreterInstruction instruction)
        {
            //set global mem Length return G1
            m_globalMemoryLength =
                ((UInt32)((instruction.actionFields[0] << 16) & 0x00FF0000) |
                (UInt32)((instruction.actionFields[1] << 8) & 0x0000FF00) |
                (UInt32)(instruction.actionFields[2] & 0x000000FF) |
                (UInt32)((instruction.actionFields[3] << 24) & 0xFF000000));
            return instruction.gotoFields[1];
        }
        public byte SetGlobalHeaderLength(Interpreter.InterpreterInstruction instruction)
        {
            //set global header Length return G1
            m_globalHeaderLength = (UInt32)
                ((UInt32)((instruction.actionFields[0] << 16) & 0x00FF0000) |
                (UInt32)((instruction.actionFields[1] << 8) & 0x0000FF00) |
                (UInt32)(instruction.actionFields[2] & 0x000000FF) |
                (UInt32)((instruction.actionFields[3] << 24) & 0xFF000000));
            return instruction.gotoFields[1];
        }

//TO DO only supported by KWP2000
        //public byte IgnoreResponsesForMilliseconds(Interpreter.InterpreterInstruction instruction);

        public byte OverrideUtilityFileMessageLengthField(Interpreter.InterpreterInstruction instruction)
        {
            if ((instruction.actionFields[0] == 0x00) & (instruction.actionFields[1] == 0x00))
            {//revert to utility file specified message length
                m_header.effectiveDataBytesPerMessage = m_header.dataBytesPerMessage;
            }
            else
            {
                m_header.effectiveDataBytesPerMessage = (UInt16)
                    ((UInt16)(instruction.actionFields[0] * 256) +
                    (UInt16)(instruction.actionFields[1]));
            }
            return instruction.gotoFields[1];
        }
        public byte NoOpOpCode(Interpreter.InterpreterInstruction instruction)
        {//return the next step... oxy moron?
            return (byte) (instruction.step + 1);
        }
        public byte GotoFieldContinuation(Interpreter.InterpreterInstruction instruction)
        {//this cannot be a goto field of an op code - continuation of previous steps goto
            //if we end up here, this is an error - only allowed to indicate goto field continuation
            if (m_logger != null)
            {
                m_logger.Log("ERROR:  " + m_ecuName + "::OpCodeHandler: Goto Field Continuation called");
            }
            m_currentOpCodeResult.m_promptOperator = true;
            m_currentOpCodeResult.m_prompt = "Programming Failed: Goto Cont. Invalid";
            m_currentOpCodeResult.m_result = false;
            m_stopProgrammingSession = true;
            //handle goto continuation functionality elsewhere
            return 0xFF;
        }
        public byte SetAndDecrementCounter(Interpreter.InterpreterInstruction instruction)
        {
            if (m_counters[instruction.actionFields[0]] == 0xFF)
            {//counter reseted set value to loop limit
                m_counters[instruction.actionFields[0]] = instruction.actionFields[1];
            }
            m_counters[instruction.actionFields[0]]--;
            if (m_counters[instruction.actionFields[0]] > 0x00)
            {//decrement counter goto g1                
                return instruction.gotoFields[1];
            }
            else
            {//counter expired goto g3
                return instruction.gotoFields[3];
            }
        }
        public byte DelayForXXSeconds(Interpreter.InterpreterInstruction instruction)
        {
            int delayTimeS = 0;
            if (instruction.actionFields[3] == 1)
            {//ac0 time in minutes
                delayTimeS = instruction.actionFields[0] * 60;
            }
            else
            {
                delayTimeS = instruction.actionFields[0];
            }
            if (instruction.actionFields[1] == 0x01 || m_header.interpType != Interpreter.InterpreterType.CLASS_2)
            {//start background message send
//not necessary handled in flash station software
                m_stopTesterPresentThread = false;
                //start serial thread to allow barcode input
                ThreadStart taskDelegate = null;
                taskDelegate = new ThreadStart(TesterPresentThread);
                Thread taskThread = new Thread(taskDelegate);
                taskThread.Start();              
            }
            System.Threading.Thread.Sleep(delayTimeS * 1000);
            if (instruction.actionFields[1] == 0x01 || m_header.interpType != Interpreter.InterpreterType.CLASS_2)
            {//stop background message send
//not necessary handled in flash station software
                m_stopTesterPresentThread = true;
            }
            return instruction.gotoFields[1];
        }
        public byte ResetCounter(Interpreter.InterpreterInstruction instruction)
        {
            if (instruction.actionFields[0] == 0xFF)
            {//reset all counters
                for (int x = 0; x < m_counters.Length; x++)
                {
                    m_counters[x] = 0xFF;
                }
            }
            else
            {
                m_counters[instruction.actionFields[0]] = 0xFF;
            }
            return instruction.gotoFields[1];
        }
        public byte EndWithSuccess(Interpreter.InterpreterInstruction instruction)
        {
            if (m_logger != null)
            {
                m_logger.Log("INFO:  " + m_ecuName + "::OpCodeHandler: End With Success Reached");
            }
            m_stopProgrammingSession = true;
            m_currentOpCodeResult.m_promptOperator = true;
            m_currentOpCodeResult.m_result = true;
            m_currentOpCodeResult.m_prompt = "Programming Success";
            return 0xFF;
        }



        //***************************************************************************************
        //GMLAN Op Code Operations
        //***************************************************************************************

        //Utility functions
        public byte GMLANResponseProcessing(List<byte> effectiveGotoFields, List<byte> data)
        {//use module response and goto fields to determine correct action to take
            bool specialCase7F = false;
            if ((effectiveGotoFields != null) && (effectiveGotoFields.Count > 0))
            {
                //null value for data will indicate no response
                if ((data != null) && (data.Count > 0))
                {//response recieved - handle accordingly
                    if (data[0] == 0x7F)
                    {//negative response received
                        //data[1] is the request ID use this to handle neg response value processing
                        //if neg response value equals pos response id - skip first goto indicator
                        specialCase7F = ((data[1] + 0x40) == data[2]);
                        for (int x = 0; x < effectiveGotoFields.Count; x++)
                        {
                            if (x % 2 == 0)
                            {//0,2,4 ... are the comparison indexes
                                if (effectiveGotoFields[x] == data[2])
                                {//return this gotoindex
                                    if (!specialCase7F)
                                    {
                                        return effectiveGotoFields[x + 1];
                                    }
                                    else
                                    {//this will allow us to skip the first match for the special 7f response handling
                                        specialCase7F = false;
                                    }
                                }
                            }
                        }
                    }//end negative response handling
                    else
                    {
                        for (int x = 0; x < effectiveGotoFields.Count; x++)
                        {
                            if (x % 2 == 0)
                            {//0,2,4 ... are the comparison indexes
                                if (effectiveGotoFields[x] == data[0])
                                {//return this gotoindex
                                    return effectiveGotoFields[x + 1];
                                }
                            }
                        }
                    }
                    //default to find 0xFF goto if no match found
                    for (int x = 0; x < effectiveGotoFields.Count; x++)
                    {
                        if (x % 2 == 0)
                        {//0,2,4 ... are the comparison indexes
                            if (effectiveGotoFields[x] == 0xFF)
                            {//return this gotoindex
                                return effectiveGotoFields[x + 1];
                            }
                        }
                    }
                }
                else
                {//no response received look for 0xFD no response goto indicator
                    if (data != null)
                    {
                        for (int x = 0; x < effectiveGotoFields.Count; x++)
                        {
                            if (x % 2 == 0)
                            {//0,2,4 ... are the comparison indexes
                                if (effectiveGotoFields[x] == 0xFD)
                                {//return this gotoindex
                                    return effectiveGotoFields[x + 1];
                                }
                            }
                        }
                    }
                }
            }
            //Severe Error condition - no goto match found
            if (m_logger != null)
            {
                m_logger.Log("ERROR:  " + m_ecuName + "::OpCodeHandler: Communication Failure: No Goto Match found");
                if (data != null)
                {
                    int index = 0;
                    foreach (byte b in data)
                    {
                        if (m_logger != null)
                        {
                            m_logger.Log("INFO:  " + m_ecuName + "::OpCodeHandler: Data["+ index +"]: " + b.ToString());
                        }
                        index++;
                    }
                    index = 0;
                    foreach (byte b in effectiveGotoFields)
                    {
                        if (m_logger != null)
                        {
                            m_logger.Log("INFO:  " + m_ecuName + "::OpCodeHandler: GotoField[" + index + "]: " + b.ToString());
                        }
                        index++;
                    }
                }
                else
                {
                    m_logger.Log("ERROR:  " + m_ecuName + "::OpCodeHandler: Data is NULL!");
                }
            }
            m_stopProgrammingSession = true;
            m_currentOpCodeResult.m_result = false;
            m_currentOpCodeResult.m_promptOperator = true;
            m_currentOpCodeResult.m_prompt = "Communication Failure";
            return 0xFF;
        }
        //this function is used for vehicle message op codes
        //recursive - get next step - if continuation opcode 0xF8 add gotofields to list
        public void AddGotoFields(ref List<byte> effectiveGotoBytes,Interpreter.InterpreterInstruction instruction)
        {
            //get next instruction (note instruction step numbers start at 1)
            effectiveGotoBytes.AddRange(instruction.gotoFields);
            if (instruction.step < m_instructions.Count)
            {
                Interpreter.InterpreterInstruction nextInstruction = m_instructions[instruction.step];
                if (nextInstruction.opCode == 0xF8)
                {                   
                    AddGotoFields(ref effectiveGotoBytes, nextInstruction);
                }
            }
        }
        //create message using global module response / request id and tx message
        public J2534ChannelLibrary.CcrtJ2534Defs.ECUMessage CreateECUMessage(ref List<byte> txMessage)
        {
            J2534ChannelLibrary.CcrtJ2534Defs.ECUMessage message = new CcrtJ2534Defs.ECUMessage();
            //add module request ID
            List<byte> moduleID = new List<byte>();

            moduleID.Add(0x00);
            moduleID.Add(0x00);
            moduleID.Add((byte)((m_moduleRequestID & 0xFF00)>>8));
            moduleID.Add((byte)(m_moduleRequestID & 0x00FF));            

            message.m_messageFilter.requestID = moduleID.ToArray();
            //add module response ID
            List<byte> responseID = new List<byte>();

            responseID.Add(0x00);
            responseID.Add(0x00);
            responseID.Add((byte)((m_moduleResponseID & 0xFF00) >> 8));
            responseID.Add((byte)(m_moduleResponseID & 0x00FF));

            message.m_messageFilter.responseID = responseID.ToArray();
            
            //Add Tx message
            message.m_txMessage = new List<byte>(txMessage);
            message.m_responsePendingRetries = 30;
            return message;
        }
        public void StoreDataByIdentifier(Interpreter.InterpreterInstruction instruction, byte storageLocation, 
            int dataStartIndex, ref List<byte> data)
        {
            //if positive response save data
            if (data[0] != 0x7F)
            {//fill either 2 byte or 256 based on AC3 - fill with 0x00
                List<byte> storeData = new List<byte>();
                int numBytesToStore = 0;
                for (int x = dataStartIndex; (x < data.Count); x++)
                {
                    storeData.Add(data[x]);
                }
                if (instruction.actionFields[3] == 0x01)
                {
                    if (data.Count > 2)
                    {
                        numBytesToStore = 2;
                    }
                    else
                    {
                        numBytesToStore = storeData.Count;
                    }
                    SetBytesFromIDTwoByte(storageLocation, storeData.ToArray(), numBytesToStore);
                }
                else
                {
                    if (data.Count > BYTE_STORAGE_MAX)
                    {
                        numBytesToStore = BYTE_STORAGE_MAX;
                    }
                    else
                    {
                        numBytesToStore = storeData.Count;
                    }
                    SetBytesFromID(storageLocation, storeData.ToArray(), numBytesToStore);
                }
            }
        }

        //GMLAN Op code Functions
        public byte SetupGlobalVariables(Interpreter.InterpreterInstruction instruction)
        {
            if (instruction.actionFields[0] != 0x00)
            {
                if (instruction.actionFields[0] == 0xFE)
                {//exit fatal error
                    if (m_logger != null)
                    {
                        m_logger.Log("ERROR:  " + m_ecuName + "::OpCodeHandler: Programming failed: global target address is 0xFE");
                    }
                    m_currentOpCodeResult.m_promptOperator = true;
                    m_currentOpCodeResult.m_prompt = "Failure: Global Target = 0xFE";
                    m_currentOpCodeResult.m_result = false;
                    m_stopProgrammingSession = true;
                    return 0xFF;
                }
                m_globalTargetAddress = instruction.actionFields[0];
            }
            if (instruction.actionFields[1] != 0x00)
            {
                m_globalSourceAddress = instruction.actionFields[1];
            }

            return instruction.gotoFields[1];
        }
        private bool m_stopTesterPresentThread;
        public void TesterPresentThread()
        {
            //create message to be sent
            List<byte> txMessage = new List<byte>();
            //opcode specific message
            txMessage.Add(0x3E);
            J2534ChannelLibrary.CcrtJ2534Defs.ECUMessage message = CreateECUMessage(ref txMessage);
            List<byte> data = new List<byte>();
            while (!m_stopProgrammingSession && !m_stopTesterPresentThread)
            {
                //m_vehicleCommInterface.AddMessageFilter(m_deviceName, m_channelName,
                //    message.m_messageFilter);
                m_vehicleCommInterface.GetECUData(m_deviceName, m_channelName, message, ref data);
                System.Threading.Thread.Sleep(TESTER_PRESENT_DELAY);
            }
        }
        public void SendTesterPresent()
        {
            //create message to be sent
            List<byte> txMessage = new List<byte>();
            //opcode specific message
            txMessage.Add(0x3E);
            J2534ChannelLibrary.CcrtJ2534Defs.ECUMessage message = CreateECUMessage(ref txMessage);
            message.m_responseExpected = false;
            message.m_rxTimeout = 10;
            List<byte> data = new List<byte>();
                m_vehicleCommInterface.GetECUData(m_deviceName, m_channelName, message, ref data);
        }

        public byte InitiateDiagnosticOperation(Interpreter.InterpreterInstruction instruction)
        {
            //create message to be sent
            List<byte> txMessage = new List<byte>();
            List<byte> effectiveGotoBytes = new List<byte>();
            AddGotoFields(ref effectiveGotoBytes, instruction);
            //opcode specific message
            txMessage.Add(0x10);
            txMessage.Add(instruction.actionFields[0]);
            J2534ChannelLibrary.CcrtJ2534Defs.ECUMessage message = CreateECUMessage(ref txMessage);
            List<byte> data = new List<byte>();
            //bool status = m_vehicleCommInterface.AddMessageFilter(m_deviceName, m_channelName,
            //    message.m_messageFilter);
            m_vehicleCommInterface.GetECUData(m_deviceName, m_channelName, message, ref data);
            return GMLANResponseProcessing(effectiveGotoBytes, data);
        }
        public byte ClearDTCs(Interpreter.InterpreterInstruction instruction)
        {            
            //create message to be sent
            List<byte> txMessage = new List<byte>();
            List<byte> effectiveGotoBytes = new List<byte>();
            AddGotoFields(ref effectiveGotoBytes, instruction);
            //opcode specific message
            txMessage.Add(0x04);
            J2534ChannelLibrary.CcrtJ2534Defs.ECUMessage message = CreateECUMessage(ref txMessage);
            List<byte> data = new List<byte>();
            //bool status = m_vehicleCommInterface.AddMessageFilter(m_deviceName, m_channelName,
            //    message.m_messageFilter);
            m_vehicleCommInterface.GetECUData(m_deviceName, m_channelName, message, ref data);
            return GMLANResponseProcessing(effectiveGotoBytes, data);
        }

        public byte ReadDataByIdentifier(Interpreter.InterpreterInstruction instruction)
        {
            //create message to be sent
            List<byte> txMessage = new List<byte>();
            List<byte> effectiveGotoBytes = new List<byte>();
            AddGotoFields(ref effectiveGotoBytes, instruction);
            //opcode specific message
            txMessage.Add(0x1A);
            txMessage.Add(instruction.actionFields[0]);
            J2534ChannelLibrary.CcrtJ2534Defs.ECUMessage message = CreateECUMessage(ref txMessage);
            List<byte> data = new List<byte>();
            //bool status = m_vehicleCommInterface.AddMessageFilter(m_deviceName, m_channelName,
             //   message.m_messageFilter);
            m_vehicleCommInterface.GetECUData(m_deviceName, m_channelName, message, ref data);
            
            //store the received data
            StoreDataByIdentifier(instruction, instruction.actionFields[1], 2, ref data);
            
            return GMLANResponseProcessing(effectiveGotoBytes, data);
        }

        public byte ReadDataByParameterIdentifier(Interpreter.InterpreterInstruction instruction)
        {
            //create message to be sent
            List<byte> txMessage = new List<byte>();
            List<byte> effectiveGotoBytes = new List<byte>();
            AddGotoFields(ref effectiveGotoBytes, instruction);
            //opcode specific message
            txMessage.Add(0x22);
            txMessage.Add(instruction.actionFields[0]);
            txMessage.Add(instruction.actionFields[1]);
            J2534ChannelLibrary.CcrtJ2534Defs.ECUMessage message = CreateECUMessage(ref txMessage);
            List<byte> data = new List<byte>();
            //bool status = m_vehicleCommInterface.AddMessageFilter(m_deviceName, m_channelName,
             //   message.m_messageFilter);
            m_vehicleCommInterface.GetECUData(m_deviceName, m_channelName, message, ref data);

            //store the received data
            StoreDataByIdentifier(instruction, instruction.actionFields[2], 3, ref data);

            return GMLANResponseProcessing(effectiveGotoBytes, data);
        }

        public byte SecurityAccess(Interpreter.InterpreterInstruction instruction)
        {
            //create message to be sent
            List<byte> txMessage = new List<byte>();
            List<byte> effectiveGotoBytes = new List<byte>();
            AddGotoFields(ref effectiveGotoBytes, instruction);
            //opcode specific message
            txMessage.Add(0x27);
            txMessage.Add(0x01);
            J2534ChannelLibrary.CcrtJ2534Defs.ECUMessage message = CreateECUMessage(ref txMessage);
            List<byte> data = new List<byte>();
           // bool status = m_vehicleCommInterface.AddMessageFilter(m_deviceName, m_channelName,
            //    message.m_messageFilter);
            m_vehicleCommInterface.GetECUData(m_deviceName, m_channelName, message, ref data);

//TO DO Full security access algorithm - not needed for Isuzu project -all modules will be unlocked
            if (data.Count > 3)
            {
                if (data[2] != 0x00 || data[3] != 0x00)
                {//indicate failure not currently supported
                    if (m_logger != null)
                    {
                        m_logger.Log("ERROR:  " + m_ecuName + "::OpCodeHandler: Module Security Locked");
                    }
                    m_stopProgrammingSession = true;
                    m_currentOpCodeResult.m_result = false;
                    m_currentOpCodeResult.m_promptOperator = true;
                    m_currentOpCodeResult.m_prompt = "Security Locked, ECU Cannot be Flashed";
                    return 0xFF;
                }
            }
            else
            {
                if (m_logger != null)
                {
                    m_logger.Log("ERROR:  " + m_ecuName + "::OpCodeHandler: SecurityAccess Invalid Response");
                }
                m_stopProgrammingSession = true;
                m_currentOpCodeResult.m_result = false;
                m_currentOpCodeResult.m_promptOperator = true;
                m_currentOpCodeResult.m_prompt = "Security unlock Invalid Response";
                return 0xFF;
            }
            return GMLANResponseProcessing(effectiveGotoBytes, data);
        }

        public byte RequestDownload(Interpreter.InterpreterInstruction instruction)
        {
            //create message to be sent
            List<byte> txMessage = new List<byte>();
            List<byte> effectiveGotoBytes = new List<byte>();

            AddGotoFields(ref effectiveGotoBytes, instruction);
            CreateRequestDownloadMessage(instruction, ref txMessage);
            if (!m_stopProgrammingSession)
            {
                J2534ChannelLibrary.CcrtJ2534Defs.ECUMessage message = CreateECUMessage(ref txMessage);
                List<byte> data = new List<byte>();
               // bool status = m_vehicleCommInterface.AddMessageFilter(m_deviceName, m_channelName,
               //     message.m_messageFilter);
                m_vehicleCommInterface.GetECUData(m_deviceName, m_channelName, message, ref data);

                return GMLANResponseProcessing(effectiveGotoBytes, data);
            }
            else
            {
                return 0xFF;
            }

        }
        public void CreateRequestDownloadMessage(Interpreter.InterpreterInstruction instruction,
                                                    ref List<byte> txMessage)
        {
            UInt32 memSize = 0x00;
            UInt16 lengthSize = 0x00;
            //opcode specific message
            txMessage.Add(0x34);
            txMessage.Add(instruction.actionFields[0]);
            //Set memory size
            if ((instruction.actionFields[3] & 0x30) == 0x00)
            {// if AC3 and 30 is false - find routine indicated by ac1 - set mem size to routine length
                memSize = m_routines[instruction.actionFields[1] - 1].length;
            }
            else
            {
                if ((instruction.actionFields[3] & 0x30) == 0x10)
                {// if ac3 and 0x10 is true set memSize to length from header
                    memSize = m_header.dataBytesPerMessage;
                }
                else if((instruction.actionFields[3] & 0x30) == 0x20)
                {//set memory size to global length
                    memSize = m_globalMemoryLength;
                }
                else if ((instruction.actionFields[3] & 0x30) == 0x30)
                {//set memory size to size of calibration file
                    memSize = (uint)m_calibrationModules[instruction.actionFields[1] - 1].Count;
                }
                else
                {//end with error not supported
                    if (m_logger != null)
                    {
                        m_logger.Log("ERROR:  " + m_ecuName + "::OpCodeHandler: Request Download Type Not Supported");
                    }
                    m_stopProgrammingSession = true;
                    m_currentOpCodeResult.m_result = false;
                    m_currentOpCodeResult.m_promptOperator = true;
                    m_currentOpCodeResult.m_prompt = "Failure: Request DL Type Not Supported";
                }
            }
            //Set length size
            if ((instruction.actionFields[3] & 0x0F) != 0x00)
            {//if ac3 and 0f is true set lengthsize to ac3 and 0f
                lengthSize = (UInt16)(instruction.actionFields[3] & 0x0F);
            }
            else
            {//set length to type of addressing
                lengthSize = m_header.addType;
            }
            //set up message using memory size
            if (lengthSize == 0x02)
            {//HB tx[2] LB tx[3]
                txMessage.Add((byte)((memSize & 0xFF00) >> 8));
                txMessage.Add((byte)(memSize & 0x00FF));
            }
            else if (lengthSize == 0x03)
            {//HB tx[2] MB tx[3] LB tx[4]
                txMessage.Add((byte)((memSize & 0x00FF0000) >> 16));
                txMessage.Add((byte)((memSize & 0x0000FF00) >> 8));
                txMessage.Add((byte)(memSize & 0x000000FF));
            }
            else if (lengthSize == 0x04)
            {//HB tx[2] MHB tx[3] MLB tx[4] LB tx[5]
                txMessage.Add((byte)((memSize & 0xFF000000) >> 24));
                txMessage.Add((byte)((memSize & 0x00FF0000) >> 16));
                txMessage.Add((byte)((memSize & 0x0000FF00) >> 8));
                txMessage.Add((byte)(memSize & 0x000000FF));
            }
        }
        public byte WriteDataByIdentifier(Interpreter.InterpreterInstruction instruction)
        {
            //create message to be sent
            List<byte> txMessage = new List<byte>();
            List<byte> effectiveGotoBytes = new List<byte>();
            List<byte> data = new List<byte>();
            AddGotoFields(ref effectiveGotoBytes, instruction);
            bool sendMessage = true;
            //opcode specific message
            txMessage.Add(0x3B);

            if ((instruction.actionFields[2] != 0x00) && (instruction.actionFields[3] == 0x22))
            {//if ac2 is true and ac3 is 22
                if (m_logger != null)
                {
                    m_logger.Log("ERROR:  " + m_ecuName + "::OpCodeHandler: WriteDataByIdentifier - AC2 = 00 AC3 = 22 unsupported");
                }
//Told we will not get to this situation
//Cal module need to set up new structure
                //GetCalibrationModule(instruction.actionFields[2]);
                //if (m_stopProgrammingSession)
               // {
                    return 0xFF;
                //}
                //Skip globalheaderlength bytes
                //read block count
//TO DO handle this case
            }
            else
            {
                if ((instruction.actionFields[3] & 0xF0) == 0x00)
                { //set data identifier to ac0
                    txMessage.Add(instruction.actionFields[0]);
                    //if lower nibble ac3 is 0
                    if ((instruction.actionFields[3] & 0x0F) == 0x00)
                    {//copy internal VIT data based on AC0 into message
                        //Isuzu hard code handling
                        List<byte> txData = new List<byte>();
                        if (instruction.actionFields[0] == 0x90)
                        {//get VIN - ascii representation
                            txData = stringToASCIIByteArray(GetVIT2Data(0x41)).ToList();
                        }
                        else if (instruction.actionFields[0] == 0x98)
                        {//Tester SN
                            txData.Add(0x00);
                            txData.Add(0x00);
                            txData.Add(0x00);
                            txData.Add(0x00);
                            txData.Add(0x00);
                            txData.Add(0x00);
                            txData.Add(0x00);
                            txData.Add(0x00);
                            txData.Add(0x00); 
                            txData.Add(0x00);
                        }
                        else if (instruction.actionFields[0] == 0x99)
                        {//Programming Date
                            int year = DateTime.Today.Year;
                            int month = DateTime.Today.Month;
                            int day = DateTime.Today.Day;
                            txData.AddRange(Year2Bcd(year));
                            txData.Add(Month2Bcd(month));
                            txData.Add(Day2Bcd(day));
                        }
                        txMessage.AddRange(txData);
                    }
                    else
                    {//copy internal VIT data based on AC1 into message
                        txMessage.AddRange(stringToBCD(GetVIT2Data(instruction.actionFields[1])));
                    }
                    //send message
                }
                else if ((instruction.actionFields[3] & 0xF0) == 0x10)
                {//if lower nible ac3 is 1
                    if ((instruction.actionFields[3] & 0x0F) == 0x01)
                    {//set identifier to first routine data byte and copy remaining data bytes into message
                        txMessage.AddRange(GetRoutineData(instruction.actionFields[1]));
                    }
                    else
                    {//set identifier to ac0 - copy routine data into message
                        txMessage.Add(instruction.actionFields[0]);
                        txMessage.AddRange(GetRoutineData(instruction.actionFields[1]));
                    }
                    //send message
                }
                else if ((instruction.actionFields[3] & 0xF0) == 0x30)
                {//set identifier to ac0
                    txMessage.Add(instruction.actionFields[0]);
                    //copy ac2 num bytes from stored info where ac1 is the stored info id
                    byte [] temp = GetBytesFromID(instruction.actionFields[1]);
                    for(int x = 0; x < instruction.actionFields[2]; x++)
                    {
                        txMessage.Add(temp[x]);
                    }
                    //send message
                }
                else if ((instruction.actionFields[3] & 0xF0) == 0x40)
                {//set identifier to ac0
                    //place last 8 digits of vin in message
//TO DO if last 8 of VIN are 0x00 do NOT send message and simulate positive response
                    //if last 8 of vin == 0x00
                    sendMessage = false;
                    //simulate positive response
                }
                else 
                {//fatal error
                }
                if (m_stopProgrammingSession)
                {//error setting up message
                    return 0xFF;
                }
                if (sendMessage)
                {
                    J2534ChannelLibrary.CcrtJ2534Defs.ECUMessage message = CreateECUMessage(ref txMessage);

                   // bool status = m_vehicleCommInterface.AddMessageFilter(m_deviceName, m_channelName,
                   //     message.m_messageFilter);
                    m_vehicleCommInterface.GetECUData(m_deviceName, m_channelName, message, ref data);
                }
            }            
            return GMLANResponseProcessing(effectiveGotoBytes, data);
        }

        public byte[] Year2Bcd(int year)
        {
            if (year < 0 || year > 9999)
            {
                if (m_logger != null)
                {
                    m_logger.Log("ERROR:  " + m_ecuName + "::OpCodeHandler: Year Out of range");
                }
                m_stopProgrammingSession = true;
                m_currentOpCodeResult.m_result = false;
                m_currentOpCodeResult.m_promptOperator = true;
                m_currentOpCodeResult.m_prompt = "Failure: Year Retrieved out of Range";
                return null;
            }
            int bcd = 0;
            for (int digit = 0; digit < 4; ++digit)
            {
                int nibble = year % 10;
                bcd |= nibble << (digit * 4);
                year /= 10;
            }
            return new byte[] { (byte)((bcd >> 8) & 0xff), (byte)(bcd & 0xff) };
        }

        public byte Month2Bcd(int month)
        {
            if (month < 0 || month > 12)
            {
                if (m_logger != null)
                {
                    m_logger.Log("ERROR:  " + m_ecuName + "::OpCodeHandler: Month Out of range");
                }
                m_stopProgrammingSession = true;
                m_currentOpCodeResult.m_result = false;
                m_currentOpCodeResult.m_promptOperator = true;
                m_currentOpCodeResult.m_prompt = "Failure: Month Retrieved out of Range";
                return 0xFF;
            }
            int bcd = 0;
            for (int digit = 0; digit < 2; ++digit)
            {
                int nibble = month % 10;
                bcd |= nibble << (digit * 4);
                month /= 10;
            }
            return (byte)(bcd & 0xff);
        }

        public byte Day2Bcd(int day)
        {
            if (day < 0 || day > 31)
            {
                if (m_logger != null)
                {
                    m_logger.Log("ERROR:  " + m_ecuName + "::OpCodeHandler: Day Out of range");
                }
                m_stopProgrammingSession = true;
                m_currentOpCodeResult.m_result = false;
                m_currentOpCodeResult.m_promptOperator = true;
                m_currentOpCodeResult.m_prompt = "Failure: Day Retrieved out of Range";
                return 0xFF;
            }
            int bcd = 0;
            for (int digit = 0; digit < 2; ++digit)
            {
                int nibble = day % 10;
                bcd |= nibble << (digit * 4);
                day /= 10;
            }
            return (byte)(bcd & 0xff);
        }


        public byte SetCommunicationsParameters(Interpreter.InterpreterInstruction instruction)
        {
            bool success = false;
            if (instruction.actionFields[0] == 0x00)
            {//default behavior use controller's flow control frame for subnets specified in AC1
                success = m_vehicleCommInterface.SetSTMin(m_deviceName, m_channelName, 0x00);
                if (success)
                {
                    return instruction.gotoFields[1];
                }
                else
                {
                    return instruction.gotoFields[3];
                }
            }
            else if (instruction.actionFields[0] == 0x01)
            {//use STmin specified by AC2 for subnets specified in AC1
                success = m_vehicleCommInterface.SetSTMin(m_deviceName, m_channelName, instruction.actionFields[1]);
                if (success)
                {
                    return instruction.gotoFields[1];
                }
                else
                {
                    return instruction.gotoFields[3];
                }
            }
            else
            {
                if (m_logger != null)
                {
                    m_logger.Log("ERROR:  " + m_ecuName + "::OpCodeHandler: Failure Setting STMIN Invalid Utility File Instruction");
                }
                m_stopProgrammingSession = true;
                m_currentOpCodeResult.m_result = false;
                m_currentOpCodeResult.m_promptOperator = true;
                m_currentOpCodeResult.m_prompt = "Failure Setting STMIN";
                return 0xFF;
            }
        }
        public byte ReportProgrammedStateAndSaveResponse(Interpreter.InterpreterInstruction instruction)
        {
            //create message to be sent
            List<byte> txMessage = new List<byte>();
            List<byte> effectiveGotoBytes = new List<byte>();
            AddGotoFields(ref effectiveGotoBytes, instruction);
            //opcode specific message
            txMessage.Add(0xA2);
            J2534ChannelLibrary.CcrtJ2534Defs.ECUMessage message = CreateECUMessage(ref txMessage);
            List<byte> data = new List<byte>();
            //bool status = m_vehicleCommInterface.AddMessageFilter(m_deviceName, m_channelName,
           //     message.m_messageFilter);
            m_vehicleCommInterface.GetECUData(m_deviceName, m_channelName, message, ref data);
            if (data[0] != 0x7F)
            {
                byte[] storeData = new byte[2] {0x00,0x00};
                //Byte 0 gets 0 stored,
                //Byte 1 gets value returned by response
                if (data.Count > 1)
                {
                    storeData[1] = data[1];
                }
                SetBytesFromIDTwoByte(instruction.actionFields[1], storeData, 2);
            }
            return GMLANResponseProcessing(effectiveGotoBytes, data);
        }
        public byte ReadDataByPacketIdentifier(Interpreter.InterpreterInstruction instruction)
        {
//TO DO VIT2 Data handling
//TO DO Check car daq for info on uudt messages for special handling
            //create message to be sent
            List<byte> txMessage = new List<byte>();
            List<byte> effectiveGotoBytes = new List<byte>();
            AddGotoFields(ref effectiveGotoBytes, instruction);
            //opcode specific message
            txMessage.Add(0xAA);
            txMessage.Add(0x01);
            txMessage.Add(instruction.actionFields[0]);
            J2534ChannelLibrary.CcrtJ2534Defs.ECUMessage message = CreateECUMessage(ref txMessage);
            List<byte> data = new List<byte>();
            //bool status = m_vehicleCommInterface.AddMessageFilter(m_deviceName, m_channelName,
            //    message.m_messageFilter);
            bool status = m_vehicleCommInterface.GetECUData(m_deviceName, m_channelName, message, ref data);
            if (status)
            {
                int dataLength = instruction.actionFields[2];
                //subtract 1 from count because identifier is not included in data to be copied
                if (dataLength > data.Count - 1)
                {//is ac2 specifies more bytes to copy than available set num bytes to copy to num bytes available
                    dataLength = data.Count - 1;
                }
                byte[] storeData = new byte[BYTE_STORAGE_MAX];
                if ((instruction.actionFields[3] & 0x80) == 0x80)
                {//Treat data as a 1..4 byte number convert into a string as decimal to temp buffer
//TO DO                    //set number of bytes to copy to string length of string in temp buffer
                }
                else
                {//Copy specified num bytes into temporary buffer
                    for (int x = 0; x < dataLength; x++)
                    {
                        storeData[x] = data[x + 1];
                    }
                }

                if (instruction.actionFields[1] < MAX_NUM_BUFFERS)
                {
                    SetBytesFromID(instruction.actionFields[1], storeData, storeData.Length);
                }
//VIT2 NOT SUPPORTED FOR ISUZU
                else if (instruction.actionFields[1] ==0x90)
                {//copy temp buffer to VIN in VIT2
                    if (m_logger != null)
                    {
                        m_logger.Log("ERROR:  " + m_ecuName + "::OpCodeHandler: VIT2 data not supported");
                    }
//TO DO VIT2
                }
                else if (instruction.actionFields[1] == 0x91)
                {//Copy Temp buffer to Vehicle Manufacturer ECU Hardware Number in VIT2
                    if (m_logger != null)
                    {
                        m_logger.Log("ERROR:  " + m_ecuName + "::OpCodeHandler: VIT2 data not supported");
                    }
//TO DO VIT2
                }
                else if (instruction.actionFields[1] == 0x98)
                {//Copy Temp buffer to Repair Shop Code or Serial number in VIT2
                    if (m_logger != null)
                    {
                        m_logger.Log("ERROR:  " + m_ecuName + "::OpCodeHandler: VIT2 data not supported");
                    }
//TO DO VIT2           
                }
                else if (instruction.actionFields[1] == 0x99)
                {//Copy temp buffer to programming date in VIT2
                    if (m_logger != null)
                    {
                        m_logger.Log("ERROR:  " + m_ecuName + "::OpCodeHandler: VIT2 data not supported");
                    }
//TO DO VIT2
                }
                else if (instruction.actionFields[1] == 0xCB)
                {//Copy temp buffer to End Model Number in VIT2
                    if (m_logger != null)
                    {
                        m_logger.Log("ERROR:  " + m_ecuName + "::OpCodeHandler: VIT2 data not supported");
                    }
//TO DO VIT2
                }
                else if (instruction.actionFields[1] == 0xCC)
                {//Copy temp buffer to system supplier ECU hardware number in VIT2
                    if (m_logger != null)
                    {
                        m_logger.Log("ERROR:  " + m_ecuName + "::OpCodeHandler: VIT2 data not supported");
                    }
//TO DO VIT2
                }
            }
//TO DO UUDT RESPONSE PROCESSING positive response goto indicated by DPID, not 0xEA as expected
            return GMLANResponseProcessing(effectiveGotoBytes, data);
        }
        public byte RequestDeviceControl(Interpreter.InterpreterInstruction instruction)
        {
            //create message to be sent
            List<byte> txMessage = new List<byte>();
            List<byte> effectiveGotoBytes = new List<byte>();
            AddGotoFields(ref effectiveGotoBytes, instruction);
            //opcode specific message
            txMessage.Add(0xAE);
            txMessage.Add(instruction.actionFields[0]);
//TO DO : is this correct?
            txMessage.AddRange(m_routines[instruction.actionFields[1]].data);

            J2534ChannelLibrary.CcrtJ2534Defs.ECUMessage message = CreateECUMessage(ref txMessage);
            List<byte> data = new List<byte>();
            //bool status = m_vehicleCommInterface.AddMessageFilter(m_deviceName, m_channelName,
            //    message.m_messageFilter);
            m_vehicleCommInterface.GetECUData(m_deviceName, m_channelName, message, ref data);
            return GMLANResponseProcessing(effectiveGotoBytes, data);
        }
        public void LoadBlockTransferData(Interpreter.InterpreterInstruction instruction, ref List<byte> data)
        {
            if ((instruction.actionFields[0] == 0x00))
            {//if ac0 (cal id) is false
                //use data from the routine indicated by value ac1
                data.AddRange(m_routines[instruction.actionFields[1] - 1].data);
            }
            else
            {//use data from the calibration file indicated by value ac0
                data.AddRange(GetCalibrationModule((UInt16)(instruction.actionFields[0] - 1)));
            }
        }
        public uint DetermineBlockTransferLengthSize(Interpreter.InterpreterInstruction instruction)
        {
            uint lengthSize = 0x00;
            if ((instruction.actionFields[3] & 0x0F) != 0x00)
            {//set lengthsize to ac3 & 0F
                lengthSize = (uint)(instruction.actionFields[3] & 0x0F);
            }
            else
            {//Set Length size to Type of addressing
                lengthSize = m_header.addType;
            }
            return lengthSize;
        }
        public uint DetermineBlockTransferStartingAddress(Interpreter.InterpreterInstruction instruction)
        {
            uint startingAddress = 0x00;
            if ((instruction.actionFields[3] & 0x30) == 0x00)
            {//if ac3 and 30 is false find the routine indicated by value AC1
                //set starting address to routine address
                startingAddress = m_routines[instruction.actionFields[1] - 1].address;
            }
            else
            {
                if ((instruction.actionFields[3] & 0x10) == 0x10)
                {//ac3 and 10 is true
                    //set starting address from header
                    startingAddress = m_header.dataAddressInfo;
                }
                else
                {//set starting address to global address
                    startingAddress = m_globalMemoryAddress;
                }
            }
            return startingAddress;
        }
        public void CreateBlockTransferMessages(Interpreter.InterpreterInstruction instruction, ref List<J2534ChannelLibrary.CcrtJ2534Defs.ECUMessage> ecuMessages)
        {
            //all messages tx initial bytes
            List<byte> txMessageIDs = new List<byte>();
            //cal or routine data that will be split up for download
            List<byte> data = new List<byte>();
            uint lengthSize;
            uint startingAddress;
            //opcode specific message
            txMessageIDs.Add(0x36);
            //Set Level
            if ((instruction.actionFields[2] & 0x30) == 0x30)
            {//if ac2 and 30 is true set level of operation to 80 (dl and exec)
                txMessageIDs.Add(0x80);
            }
            else
            {//set level of op to 00 (dl)
                txMessageIDs.Add(0x00);
            }
            //Determine address
            startingAddress = DetermineBlockTransferStartingAddress(instruction);
            //Determine Length
            lengthSize = DetermineBlockTransferLengthSize(instruction);
            if ((instruction.actionFields[2] & 0x20) == 0x00)
            {//if ac2 & 0x20 is false (not execute only)
                //load data
                LoadBlockTransferData(instruction, ref data);
                uint maxDataBytes = 0x00;
                //max data per message was 4093 - ls - 1;
                maxDataBytes = m_header.dataBytesPerMessage - lengthSize - 2;
                uint bytesTransfered = 0x00;
                List<byte> txMessage = new List<byte>();
                for (uint x = 0; (x < data.Count); x += bytesTransfered)
                {//while more data to download
                    //set up message using memory size
                    txMessage.Clear();
                    txMessage.AddRange(txMessageIDs);
                    if (lengthSize == 0x02)
                    {//HB tx[2] LB tx[3]
                        txMessage.Add((byte)((startingAddress & 0xFF00) >> 8));
                        txMessage.Add((byte)(startingAddress & 0x00FF));
                    }
                    else if (lengthSize == 0x03)
                    {//HB tx[2] MB tx[3] LB tx[4]
                        txMessage.Add((byte)((startingAddress & 0x00FF0000) >> 16));
                        txMessage.Add((byte)((startingAddress & 0x0000FF00) >> 8));
                        txMessage.Add((byte)(startingAddress & 0x000000FF));
                    }
                    else if (lengthSize == 0x04)
                    {//HB tx[2] MHB tx[3] MLB tx[4] LB tx[5]
                        txMessage.Add((byte)((startingAddress & 0xFF000000) >> 24));
                        txMessage.Add((byte)((startingAddress & 0x00FF0000) >> 16));
                        txMessage.Add((byte)((startingAddress & 0x0000FF00) >> 8));
                        txMessage.Add((byte)(startingAddress & 0x000000FF));
                    }
                    if ((x + maxDataBytes >= data.Count))
                    {//if last packet to dl or global header length is not 0 and splits the module
                        //copy remaining data into message
                        bytesTransfered = (uint)(data.Count - x);
                        txMessage.AddRange(data.GetRange((int)x, (int)(data.Count - x)));
                    }
                    else if(m_globalHeaderLength != 0)
                    {
                        bytesTransfered = m_globalHeaderLength;
                        txMessage.AddRange(data.GetRange((int)x, (int)m_globalHeaderLength));
                    }
                    else
                    {//copy next block of data into message
                        bytesTransfered = maxDataBytes;
                        txMessage.AddRange(data.GetRange((int)x, (int)maxDataBytes));
                    }
                    if ((instruction.actionFields[2] & 0x0F) == 0x00)
                    {//if ac2 & 0f is false
                        //Calculate next dl address
                        startingAddress = startingAddress + maxDataBytes;
                    }
                    J2534ChannelLibrary.CcrtJ2534Defs.ECUMessage message = CreateECUMessage(ref txMessage);
                    ecuMessages.Add(message);
                }
            }
            else
            {//execute only
                if (lengthSize == 0x02)
                {//HB tx[2] LB tx[3]
                    txMessageIDs.Add((byte)((startingAddress & 0xFF00) >> 8));
                    txMessageIDs.Add((byte)(startingAddress & 0x00FF));
                }
                else if (lengthSize == 0x03)
                {//HB tx[2] MB tx[3] LB tx[4]
                    txMessageIDs.Add((byte)((startingAddress & 0x00FF0000) >> 16));
                    txMessageIDs.Add((byte)((startingAddress & 0x0000FF00) >> 8));
                    txMessageIDs.Add((byte)(startingAddress & 0x000000FF));
                }
                else if (lengthSize == 0x04)
                {//HB tx[2] MHB tx[3] MLB tx[4] LB tx[5]
                    txMessageIDs.Add((byte)((startingAddress & 0xFF000000) >> 24));
                    txMessageIDs.Add((byte)((startingAddress & 0x00FF0000) >> 16));
                    txMessageIDs.Add((byte)((startingAddress & 0x0000FF00) >> 8));
                    txMessageIDs.Add((byte)(startingAddress & 0x000000FF));
                }
                J2534ChannelLibrary.CcrtJ2534Defs.ECUMessage message = CreateECUMessage(ref txMessageIDs);
                ecuMessages.Add(message);
            }
        }
        public byte BlockTransferToRAM(Interpreter.InterpreterInstruction instruction)
        {
            List<J2534ChannelLibrary.CcrtJ2534Defs.ECUMessage> ecuMessages = new List<J2534ChannelLibrary.CcrtJ2534Defs.ECUMessage>();
            List<byte> effectiveGotoBytes = new List<byte>();
            List<byte> recData = new List<byte>();
            bool status;
            SendTesterPresent();
            AddGotoFields(ref effectiveGotoBytes, instruction);

            CreateBlockTransferMessages(instruction, ref ecuMessages);

            if ((instruction.actionFields[2] & 0x20) == 0x00)
            {//if ac2 & 0x20 is false (not execute only)
                foreach (J2534ChannelLibrary.CcrtJ2534Defs.ECUMessage message in ecuMessages)
                {//while more data to download                  
                    recData.Clear();
                    // status = m_vehicleCommInterface.AddMessageFilter(m_deviceName, m_channelName,
                    //     message.m_messageFilter);
                    if (!m_stopProgrammingSession)
                    {
                        int messageReceiveRetries = 12;
                        message.m_responsePendingRetries = 50;
                        message.m_txTimeout = 20000;
                        message.m_rxTimeout = 250;
                        message.m_noResponseRetries = 9;
                        //only one attempt to send message
                        message.m_retries = 1;
                        m_vehicleCommInterface.AddMessageFilter(m_deviceName, m_channelName,
                            message.m_messageFilter);
                        status = m_vehicleCommInterface.GetECUData(m_deviceName, m_channelName, message, ref recData);
                        if (status)
                        {
                            if (recData[0] == 0x7F)
                            {//if neg response break for loop
                                break;
                            }
                            //rough estimate do not worry about PID and address
                            m_bytesTransmitted += message.m_txMessage.Count();
                            SendTesterPresent();
                        }
                        else
                        {
                            m_logger.Log("ERROR:  " + m_ecuName + "::Block Tx Response Not Received Continue attempts");
                            LogResponseBufferInfo();
                            for (int x = 0; x < messageReceiveRetries; x++)
                            {//attempt to receive message multiple times
                                status = m_vehicleCommInterface.ProcessMessageCAN(m_deviceName, m_channelName, message, ref recData);
                                if (status) break;
                            }
                            if (status)
                            {
                                if (recData[0] == 0x7F)
                                {//if neg response break for loop
                                    break;
                                }
                                //rough estimate do not worry about PID and address
                                m_bytesTransmitted += message.m_txMessage.Count();
                                SendTesterPresent();
                            }
                            else
                            {
                                //handle like 85 programming error - utility file will handle accordingly
                                m_logger.Log("ERROR:  " + m_ecuName + "::DL Error forcing 85 negative response");
                                recData.Add(0x7F);
                                recData.Add(0x36);
                                recData.Add(0x85);
                                LogResponseBufferInfo();
                                break;
                            }
                        }
                    }
                    else
                    {
                        return 0xFF;
                    }

                }
            }
            else
            {//execute only
                // bool status = m_vehicleCommInterface.AddMessageFilter(m_deviceName, m_channelName,
                //     message.m_messageFilter);
                m_vehicleCommInterface.GetECUData(m_deviceName, m_channelName, ecuMessages[0], ref recData);
            }
            return GMLANResponseProcessing(effectiveGotoBytes, recData);
        }

        public void LogResponseBufferInfo()
        {//Log detailed information for debugging
            List<CcrtJ2534Defs.Response> responses = new List<CcrtJ2534Defs.Response>();
            responses = m_vehicleCommInterface.GetResponseBuffer(m_deviceName, m_channelName);
            List<CcrtJ2534Defs.Response> removedResponses = new List<CcrtJ2534Defs.Response>();
            removedResponses = m_vehicleCommInterface.GetRemovedResponsesBuffer(m_deviceName, m_channelName);
            m_logger.Log("INFO:  " + m_ecuName + "::Response Buffer: ");
            foreach (CcrtJ2534Defs.Response response in responses)
            {
                m_logger.Log(BitConverter.ToString(response.m_rxMessage.ToArray()));
            }
            m_logger.Log("INFO:  " + m_ecuName + "::Removed Response Buffer: ");
            foreach (CcrtJ2534Defs.Response response in removedResponses)
            {
                m_logger.Log(BitConverter.ToString(response.m_rxMessage.ToArray()));
            }
        }

    }
}
