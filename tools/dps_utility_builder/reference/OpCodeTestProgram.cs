using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using UtilityFileInterpreter;

namespace UtilityFileInterpreterViewer
{
    public partial class OpCodeTestProgram : Form
    {
        public Interpreter m_interp;
        public Interpreter.Header m_header;
        public Interpreter.InterpreterInstruction m_instruction;
        public VehicleCommServer.IVehicleCommServerInterface m_vehicleCommInterface;
        public string m_deviceName;
        public string m_channelName;
        public OpCodeTestProgram()
        {
            InitializeComponent();
            m_deviceName = "CarDAQ PLUS";
            m_channelName = "GMLAN";
            m_vehicleCommInterface = new VehicleCommServer.VehicleCommServerInterface();
            List<string> calibrationFileNames = new List<string>();
            m_interp = new Interpreter(m_vehicleCommInterface, ref calibrationFileNames, m_deviceName, m_channelName);
            m_header = new Interpreter.Header();
            //initialize header
            m_header.checksum = 0x00;
            m_header.moduleID = 0x00;
            m_header.partNo = 0x00; //utility file part number
            m_header.designLevel = 0x00;
            m_header.headerType = 0x00; //0x0000 or offset to 2nd section
            m_header.interpType = Interpreter.InterpreterType.GMLAN; //indicates comm protocol
            m_header.routineSectionOffset = 0x00;
            m_header.addType = 0x04; //2,3,or 4 (4 for GMLAN)
            m_header.dataAddressInfo = 0x00;
            m_header.dataBytesPerMessage = 0x08;
            //data bytes per message can be changed by op codes
            m_header.effectiveDataBytesPerMessage = 0x08;


            m_instruction = new Interpreter.InterpreterInstruction();

        }
        public void InitInstruction()
        {//zero out instruction for next test
            m_instruction.step = 0x01;
            m_instruction.opCode = 0x00;
            m_instruction.actionFields = new byte[4] { 0x00, 0x00, 0x00, 0x00 };
            m_instruction.gotoFields = new byte[10] { 0x00, 0x00, 0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0x04, 0x00 };
            m_interp.m_opCodeHandler.clearSavedBytes();
            m_interp.m_opCodeHandler.clearSavedBytesTwoByte();
            //initialize storage arrays to arbitrary test values
            byte[] savedByteArray00 = new byte[OpCodeHandler.BYTE_STORAGE_MAX];
            byte[] savedByteArray01 = new byte[OpCodeHandler.BYTE_STORAGE_MAX];
            byte[] savedByteArray02 = new byte[OpCodeHandler.BYTE_STORAGE_MAX];
            byte[] savedByteArray03 = new byte[OpCodeHandler.BYTE_STORAGE_MAX];
            byte[] savedByteArray04 = new byte[OpCodeHandler.BYTE_STORAGE_MAX];
                        
            byte[] saved2ByteArray00 = new byte[2];
            byte[] saved2ByteArray01 = new byte[2];
            byte[] saved2ByteArray02 = new byte[2];
            byte[] saved2ByteArray03 = new byte[2];
            byte[] saved2ByteArray04 = new byte[2];

            for (int x = 0 ; x < OpCodeHandler.BYTE_STORAGE_MAX; x++)
            {
                savedByteArray00[x] = Convert.ToByte(x);
                savedByteArray01[x] = Convert.ToByte(((x+1)%0xFF));
                savedByteArray02[x] = Convert.ToByte(((x+2)%0xFF));
                savedByteArray03[x] = Convert.ToByte(((x+3)%0xFF));
                savedByteArray04[x] = Convert.ToByte(((x+4)%0xFF));
            }
            for (int x = 0; x < 2; x++)
            {
                saved2ByteArray00[x] = Convert.ToByte(x+5);
                saved2ByteArray01[x] = Convert.ToByte(x + 6);
                saved2ByteArray02[x] = Convert.ToByte(x + 7);
                saved2ByteArray03[x] = Convert.ToByte(x + 8);
                saved2ByteArray04[x] = Convert.ToByte(x + 9);
            }

            m_interp.m_opCodeHandler.addSavedBytes(savedByteArray00);
            m_interp.m_opCodeHandler.addSavedBytes(savedByteArray01);
            m_interp.m_opCodeHandler.addSavedBytes(savedByteArray02);
            m_interp.m_opCodeHandler.addSavedBytes(savedByteArray03);
            m_interp.m_opCodeHandler.addSavedBytes(savedByteArray04);

            m_interp.m_opCodeHandler.addSavedBytesTwoByte(saved2ByteArray00);
            m_interp.m_opCodeHandler.addSavedBytesTwoByte(saved2ByteArray01);
            m_interp.m_opCodeHandler.addSavedBytesTwoByte(saved2ByteArray02);
            m_interp.m_opCodeHandler.addSavedBytesTwoByte(saved2ByteArray03);
            m_interp.m_opCodeHandler.addSavedBytesTwoByte(saved2ByteArray04);

            m_interp.m_opCodeHandler.m_stopProgrammingSession = false;
        }
        
        public void RunCommonOpCodeTests()
        {
            if (TestCompareBytes())
            {
                m_testDataGridView.Rows.Add("TestCompareBytes", "Pass");
            }
            else
            {
                m_testDataGridView.Rows.Add("TestCompareBytes", "Fail");
            }
            //Need Calibration module id information to test
            /*if (TestCompareChecksum())
            {
                m_testDataGridView.Rows.Add("TestCompareChecksum", "Pass");
            }
            else
            {
                m_testDataGridView.Rows.Add("TestCompareChecksum", "Fail");
            }*/

            if (TestCompareData())
            {
                m_testDataGridView.Rows.Add("TestCompareData", "Pass");
            }
            else
            {
                m_testDataGridView.Rows.Add("TestCompareData", "Fail");
            }

            if (TestChangeData())
            {
                m_testDataGridView.Rows.Add("TestChangeData", "Pass");
            }
            else
            {
                m_testDataGridView.Rows.Add("TestChangeData", "Fail");
            }

            if (TestInterpreterIdentifier())
            {
                m_testDataGridView.Rows.Add("TestInterpreterIdentifier", "Pass");
            }
            else
            {
                m_testDataGridView.Rows.Add("TestInterpreterIdentifier", "Fail");
            }

            if (TestEndWithError())
            {
                m_testDataGridView.Rows.Add("TestEndWithError", "Pass");
            }
            else
            {
                m_testDataGridView.Rows.Add("TestEndWithError", "Fail");
            }

            if (TestSetGlobalMemoryAddress())
            {
                m_testDataGridView.Rows.Add("TestSetGlobalMemoryAddress", "Pass");
            }
            else
            {
                m_testDataGridView.Rows.Add("TestSetGlobalMemoryAddress", "Fail");
            }

            if (TestSetGlobalMemoryLength())
            {
                m_testDataGridView.Rows.Add("TestSetGlobalMemoryLength", "Pass");
            }
            else
            {
                m_testDataGridView.Rows.Add("TestSetGlobalMemoryLength", "Fail");
            }

            if (TestSetGlobalHeaderLength())
            {
                m_testDataGridView.Rows.Add("TestSetGlobalHeaderLength", "Pass");
            }
            else
            {
                m_testDataGridView.Rows.Add("TestSetGlobalHeaderLength", "Fail");
            }

            if (TestOverrideUtilityFileMessageLengthField())
            {
                m_testDataGridView.Rows.Add("TestOverrideUtilityFileMessageLengthField", "Pass");
            }
            else
            {
                m_testDataGridView.Rows.Add("TestOverrideUtilityFileMessageLengthField", "Fail");
            }

            if (TestNoOpOpCode())
            {
                m_testDataGridView.Rows.Add("TestNoOpOpCode", "Pass");
            }
            else
            {
                m_testDataGridView.Rows.Add("TestNoOpOpCode", "Fail");
            }

            if (TestSetAndDecrementCounter())
            {
                m_testDataGridView.Rows.Add("TestSetAndDecrementCounter", "Pass");
            }
            else
            {
                m_testDataGridView.Rows.Add("TestSetAndDecrementCounter", "Fail");
            }

            if (TestDelayForXXSeconds())
            {
                m_testDataGridView.Rows.Add("TestDelayForXXSeconds", "Pass");
            }
            else
            {
                m_testDataGridView.Rows.Add("TestDelayForXXSeconds", "Fail");
            }

            if (TestResetCounter())
            {
                m_testDataGridView.Rows.Add("TestResetCounter", "Pass");
            }
            else
            {
                m_testDataGridView.Rows.Add("TestResetCounter", "Fail");
            }

            if (TestEndWithSuccess())
            {
                m_testDataGridView.Rows.Add("TestEndWithSuccess", "Pass");
            }
            else
            {
                m_testDataGridView.Rows.Add("TestEndWithSuccess", "Fail");
            }

            if (TestDMY())
            {
                m_testDataGridView.Rows.Add("DayMonthYearBCDConversioin", "Pass");
            }
            else
            {
                m_testDataGridView.Rows.Add("DayMonthYearBCDConversioin", "Fail");
            }


        }

        public void RunGMLANOpCodeTests()
        {
            if (TestSetupGlobalAddressBytes())
            {
                m_gmlanTestsDataGridView.Rows.Add("TestSetupGlobalAddressBytes", "Pass");
            }
            else
            {
                m_gmlanTestsDataGridView.Rows.Add("TestSetupGlobalAddressBytes", "Fail");
            }
            if (TestGMLANResponseProcessing())
            {
                m_gmlanTestsDataGridView.Rows.Add("TestGMLANResponseProcessing", "Pass");
            }
            else
            {
                m_gmlanTestsDataGridView.Rows.Add("TestGMLANResponseProcessing", "Fail");
            }
            if (TestAddGotoFields())
            {
                m_gmlanTestsDataGridView.Rows.Add("TestAddGotoFields", "Pass");
            }
            else
            {
                m_gmlanTestsDataGridView.Rows.Add("TestAddGotoFields", "Fail");
            }
            if (TestCreateECUMessage())
            {
                m_gmlanTestsDataGridView.Rows.Add("TestCreateECUMessage", "Pass");
            }
            else
            {
                m_gmlanTestsDataGridView.Rows.Add("TestCreateECUMessage", "Fail");
            }
            if (TestSetupGlobalVariables())
            {
                m_gmlanTestsDataGridView.Rows.Add("TestSetupGlobalVariables", "Pass");
            }
            else
            {
                m_gmlanTestsDataGridView.Rows.Add("TestSetupGlobalVariables", "Fail");
            }
            if (TestStoreDataByIdentifier())
            {
                m_gmlanTestsDataGridView.Rows.Add("TestStoreDataByIdentifier", "Pass");
            }
            else
            {
                m_gmlanTestsDataGridView.Rows.Add("TestStoreDataByIdentifier", "Fail");
            }

            if (TestPCMBlockTransferToRAM())
            {
                m_gmlanTestsDataGridView.Rows.Add("TestPCMBlockTransferToRAM", "Pass");
            }
            else
            {
                m_gmlanTestsDataGridView.Rows.Add("TestPCMBlockTransferToRAM", "Fail");
            }

            if (TestTCMBlockTransferToRAM())
            {
                m_gmlanTestsDataGridView.Rows.Add("TestTCMBlockTransferToRAM", "Pass");
            }
            else
            {
                m_gmlanTestsDataGridView.Rows.Add("TestTCMBlockTransferToRAM", "Fail");
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
                if (data1.Length != data2.Length)
                {
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

        public bool TestCompareBytes()
        {
            bool success = true;
            byte gotoByte = 0x00;
            InitInstruction();
            m_instruction.opCode = 0x50;
            //test saved byte out of range failure           
            m_instruction.actionFields[0] = 0x14; //out of range saved byte address
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            if (gotoByte != 0xFF || !m_interp.m_opCodeHandler.m_stopProgrammingSession)
            {
                success = false;
            }
            m_interp.m_opCodeHandler.m_stopProgrammingSession = false;

            //test comparison equal goto
            m_instruction.actionFields[0] = 0x01;
            m_instruction.actionFields[1] = 0x06;
            m_instruction.actionFields[2] = 0x07;
            m_instruction.gotoFields[1] = 0x35;
            m_instruction.gotoFields[3] = 0x85;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession)
            {
                success = false;
            }

            //test comparison not equal goto
            m_instruction.actionFields[1] = 0x06;
            m_instruction.actionFields[2] = 0x08;
            m_instruction.gotoFields[1] = 0x35;
            m_instruction.gotoFields[3] = 0x85;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            if (gotoByte != 0x85 || m_interp.m_opCodeHandler.m_stopProgrammingSession)
            {
                success = false;
            }
            return success;
        }

        public bool TestCompareChecksum()
        {
            bool success = true;
            byte gotoByte = 0x00;
            InitInstruction();
            m_instruction.opCode = 0x51;

            //test comparison checksum equal goto
            //16 bit
            //stored correct answer
            m_instruction.actionFields[0] = 0x02;
//Temporary until question answered: stored bytes to sum
            m_instruction.actionFields[1] = 0x01;
            m_interp.m_opCodeHandler.m_savedBytes[2] = new byte[5] { 0x03, 0x5C, 0xFF, 0xFF, 0xFF };
            //16 bit
            m_instruction.actionFields[2] = 0x00;
            m_instruction.gotoFields[1] = 0x35;
            m_instruction.gotoFields[3] = 0x85;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession)
            {
                success = false;
            }
            //CRC-32
            //stored correct answer
            m_instruction.actionFields[0] = 0x02;
//Temporary until question answered: stored bytes to sum
            m_instruction.actionFields[1] = 0x01;
            m_interp.m_opCodeHandler.m_savedBytes[2] = new byte[7] { 0x00, 0x00, 0x03, 0x5C, 0xFF, 0xFF, 0xFF };
            //32 bit
            m_instruction.actionFields[2] = 0x02;
            m_instruction.gotoFields[1] = 0x35;
            m_instruction.gotoFields[3] = 0x85;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession)
            {
                success = false;
            }
            //CRC-32 compliment
            //stored correct answer
            m_instruction.actionFields[0] = 0x02;
            //Temporary until question answered: stored bytes to sum
            m_instruction.actionFields[1] = 0x01;
            m_interp.m_opCodeHandler.m_savedBytes[2] = new byte[7] { 0xFF, 0xFF, 0xFC, 0xA3, 0xFF, 0xFF, 0xFF };
            //32 bit complement
            m_instruction.actionFields[2] = 0x01;
            m_instruction.gotoFields[1] = 0x35;
            m_instruction.gotoFields[3] = 0x85;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession)
            {
                success = false;
            }
            return success;
        }

        public bool TestCompareData()
        {
            bool success = true;
            byte gotoByte = 0x00;
            InitInstruction();
            string vit2Data = ("0123456789ABCDEFGHIJ");
            m_instruction.opCode = (byte)OpCodeHandler.GMLANOpCodes.COMPARE_DATA;

            //clear vit2 data
            m_interp.m_opCodeHandler.clearVIT2Data();
            m_interp.m_opCodeHandler.m_savedBytes[2] = new byte[20] { 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
                                                 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A};
            m_interp.m_opCodeHandler.addVIT2Data(vit2Data);

            //test comparison equal goto
            //saved bytes ID
            m_instruction.actionFields[0] = 0x02;
            //vin type compare no conversion (at position 0)
            m_instruction.actionFields[1] = 0x01;
            m_instruction.actionFields[2] = 0x00;
            m_instruction.actionFields[3] = 0x00;
            m_instruction.gotoFields[1] = 0x35;
            m_instruction.gotoFields[3] = 0x85;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession)
            {
                success = false;
            }

            //test not equal goto
            m_interp.m_opCodeHandler.m_savedBytes[2] = new byte[20] { 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
                                                                       0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x44};
            //saved bytes ID
            m_instruction.actionFields[0] = 0x02;
            //vin type compare no conversion (at position 0)
            m_instruction.actionFields[1] = 0x01;
            m_instruction.actionFields[2] = 0x00;
            m_instruction.actionFields[3] = 0x00;
            m_instruction.gotoFields[1] = 0x35;
            m_instruction.gotoFields[3] = 0x85;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            if (gotoByte != 0x85 || m_interp.m_opCodeHandler.m_stopProgrammingSession)
            {
                success = false;
            }


            //test equal goto
            //pn type compare with ascii to 4byte usn bcd conversion
            vit2Data = "2485885505";
            m_interp.m_opCodeHandler.addVIT2Data(vit2Data);
            m_interp.m_opCodeHandler.m_savedBytes[2] = new byte[20] { 0x94, 0x2B, 0x9A, 0x41, 0x00, 0x00, 0x00, 0x00, 0x00,0x00,
                                                                      0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00};
            //saved bytes ID
            m_instruction.actionFields[0] = 0x02;
            //sw pn 1 (at position 0)
            m_instruction.actionFields[1] = 0x02;
            m_instruction.actionFields[2] = 0x01;
            m_instruction.actionFields[3] = 0x00;
            m_instruction.gotoFields[1] = 0x35;
            m_instruction.gotoFields[3] = 0x85;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession)
            {
                success = false;
            }

            //test not equal goto
            //pn type compare with ascii to 4byte usn bcd conversion
            // comparing to vit2Data = "2485885505";
            m_interp.m_opCodeHandler.m_savedBytes[2] = new byte[20] { 0x94, 0x2B, 0x9A, 0x42, 0x00, 0x00, 0x00, 0x00, 0x00,0x00,
                                                                      0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00};
            //saved bytes ID
            m_instruction.actionFields[0] = 0x02;
            //sw pn 1 (at position 0)
            m_instruction.actionFields[1] = 0x02;
            m_instruction.actionFields[2] = 0x01;
            m_instruction.actionFields[3] = 0x00;
            m_instruction.gotoFields[1] = 0x35;
            m_instruction.gotoFields[3] = 0x85;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            if (gotoByte != 0x85 || m_interp.m_opCodeHandler.m_stopProgrammingSession)
            {
                success = false;
            }

            return success;
        }

        public bool TestChangeData()
        {
            bool success = true;
            byte gotoByte = 0x00;
            InitInstruction();
            m_instruction.opCode = (byte)OpCodeHandler.GMLANOpCodes.CHANGE_DATA;
            m_instruction.gotoFields[1] = 0x35;
            m_instruction.gotoFields[3] = 0x85;
            m_instruction.actionFields[0] = 0x02;
            //byte position or shift bytes
            m_instruction.actionFields[1] = 0x13;

            m_interp.m_opCodeHandler.m_savedBytes[2] = new byte[20] { 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
                                                 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43};

            //test equal operation - equal
            m_instruction.actionFields[2] = 0x00;
            m_instruction.actionFields[3] = 0xCC;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession || 
                m_interp.m_opCodeHandler.m_savedBytes[2][19] != 0xCC)
            {
                success = false;
            }

            m_interp.m_opCodeHandler.m_savedBytes[2] = new byte[20] { 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
                                                 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43};
            
            //test equal operation - AND
            m_instruction.actionFields[2] = 0x01;
            m_instruction.actionFields[3] = 0xCC;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession ||
                m_interp.m_opCodeHandler.m_savedBytes[2][19] != 0x40)
            {
                success = false;
            }

            m_interp.m_opCodeHandler.m_savedBytes[2] = new byte[20] { 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
                                                 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43};

            //test equal operation - OR
            m_instruction.actionFields[2] = 0x02;
            m_instruction.actionFields[3] = 0xCC;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession ||
                m_interp.m_opCodeHandler.m_savedBytes[2][19] != 0xCF)
            {
                success = false;
            }

            m_interp.m_opCodeHandler.m_savedBytes[2] = new byte[20] { 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
                                                 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43};

            //test equal operation - XOR
            m_instruction.actionFields[2] = 0x03;
            m_instruction.actionFields[3] = 0xCC;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession ||
                m_interp.m_opCodeHandler.m_savedBytes[2][19] != 0x8F)
            {
                success = false;
            }

            m_interp.m_opCodeHandler.m_savedBytes[2] = new byte[20] { 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
                                                 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43};
            byte[] answer = new byte[20] { 0x43, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                                                 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00};
            //test equal operation - SHL
            m_instruction.actionFields[2] = 0x04;
            m_instruction.actionFields[3] = 0xCC;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession || !AreBytesArraysEqual(answer,m_interp.m_opCodeHandler.m_savedBytes[2].ToArray()))
            {
                success = false;
            }

            m_interp.m_opCodeHandler.m_savedBytes[2] = new byte[20] { 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
                                                 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43};
            answer = new byte[20] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                                                 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x30};
            //test equal operation - SHR
            m_instruction.actionFields[2] = 0x05;
            m_instruction.actionFields[3] = 0xCC;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession || !AreBytesArraysEqual(answer, m_interp.m_opCodeHandler.m_savedBytes[2].ToArray()))
            {
                success = false;
            }

            m_interp.m_opCodeHandler.m_savedBytes[2] = new byte[20] { 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
                                                 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43};
            Interpreter.Routine routine = new Interpreter.Routine();
            routine.address = 0x00;
            routine.length = 20;
            routine.data = new byte[20] {0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
                                                 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43};
            m_interp.m_routines.Add(routine);
            //test equal operation - copy storage buffer - load data from routine (spec by ac1) into buffer specified by AC0
            m_instruction.actionFields[1] = 0x00;
            m_instruction.actionFields[2] = 0x06;
            m_instruction.actionFields[3] = 0xCC;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession || !AreBytesArraysEqual(routine.data, m_interp.m_opCodeHandler.m_savedBytes[2].ToArray()))
            {
                success = false;
            }

            m_interp.m_opCodeHandler.m_savedBytes[2] = new byte[OpCodeHandler.BYTE_STORAGE_MAX];
            for (int x = 0; x < OpCodeHandler.BYTE_STORAGE_MAX; x++)
            {
                m_interp.m_opCodeHandler.m_savedBytes[2][x] = Convert.ToByte(x);
            }

            m_interp.m_routines.Add(routine);
            //test equal operation - copy storage buffer to storage buffer src ac1 dst ac0
            m_instruction.actionFields[0] = 0x01;
            m_instruction.actionFields[1] = 0x02;
            m_instruction.actionFields[2] = 0x08;
            m_instruction.actionFields[3] = 0xCC;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession || !AreBytesArraysEqual(m_interp.m_opCodeHandler.m_savedBytes[1].ToArray(), m_interp.m_opCodeHandler.m_savedBytes[2].ToArray()))
            {
                success = false;
            }


            return success;
        }

        public bool TestInterpreterIdentifier()
        {
            bool success = true;
            byte gotoByte = 0x00;
            InitInstruction();
            m_instruction.opCode = (byte)OpCodeHandler.GMLANOpCodes.INTERPRETER_IDENTIFIER;
            m_instruction.gotoFields[1] = 0x35;
            m_instruction.gotoFields[3] = 0x85;

            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            if (gotoByte != 0x85)
            {
                success = false;
            }

            return success;
        }

        public bool TestEndWithError()
        {
            bool success = true;
            byte gotoByte = 0x00;
            InitInstruction();
            m_instruction.opCode = (byte)OpCodeHandler.GMLANOpCodes.END_WITH_ERROR;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            if (gotoByte != 0xFF || !m_interp.m_opCodeHandler.m_stopProgrammingSession || m_interp.m_opCodeHandler.m_currentOpCodeResult.m_result)
            {
                success = false;
            }
            return success;
        }

        public bool TestDMY()
        {
            bool success = true;
            byte [] yearBCD = new byte[2];
            byte monthBCD = 0x00;
            byte dayBCD = 0x00;
            InitInstruction();

            //just checking function dateTime for format
            int testday = DateTime.Today.Day;
            int testmonth = DateTime.Today.Month;
            int testyear = DateTime.Today.Year;

            //test Year
            int year = 2011;
            yearBCD = m_interp.m_opCodeHandler.Year2Bcd(year);           
            if (m_interp.m_opCodeHandler.m_stopProgrammingSession || !m_interp.m_opCodeHandler.m_currentOpCodeResult.m_result ||
                yearBCD[0] != 0x20 || yearBCD[1] != 0x11)
            {
                success = false;
            }

            //test Month
            int month = 1;
            monthBCD = m_interp.m_opCodeHandler.Month2Bcd(month);
            if (m_interp.m_opCodeHandler.m_stopProgrammingSession || !m_interp.m_opCodeHandler.m_currentOpCodeResult.m_result ||
                monthBCD != 0x01)
            {
                success = false;
            }

            //test Day
            int day = 25;
            dayBCD = m_interp.m_opCodeHandler.Day2Bcd(day);
            if (m_interp.m_opCodeHandler.m_stopProgrammingSession || !m_interp.m_opCodeHandler.m_currentOpCodeResult.m_result || 
                dayBCD != 0x25)
            {
                success = false;
            }

            return success;
        }

        public bool TestSetGlobalMemoryAddress()
        {
            bool success = true;
            byte gotoByte = 0x00;
            InitInstruction();
            m_instruction.opCode = (byte)OpCodeHandler.GMLANOpCodes.SET_GLOBAL_MEMORY_ADDRESS;
            m_instruction.gotoFields[1] = 0x35;
            m_instruction.actionFields[0] = 0x01;
            m_instruction.actionFields[1] = 0x12;
            m_instruction.actionFields[2] = 0x23;
            m_instruction.actionFields[3] = 0x34;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            byte gMemAddress00 = (byte) (m_interp.m_opCodeHandler.m_globalMemoryAddress & 0x000000FF);
            byte gMemAddress01 = (byte) ((m_interp.m_opCodeHandler.m_globalMemoryAddress & 0x0000FF00) >> 8);
            byte gMemAddress02 = (byte) ((m_interp.m_opCodeHandler.m_globalMemoryAddress & 0x00FF0000) >> 16);
            byte gMemAddress03 = (byte) ((m_interp.m_opCodeHandler.m_globalMemoryAddress & 0xFF000000) >> 24);
            bool match = (gMemAddress00 == 0x23 ) && (gMemAddress01 == 0x12) && (gMemAddress02 == 0x01) && (gMemAddress03 == 0x34);
            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession || !match)
            {
                success = false;
            }
            return success;
        }

        public bool TestSetGlobalMemoryLength()
        {
            bool success = true;
            byte gotoByte = 0x00;
            InitInstruction();
            m_instruction.opCode = (byte)OpCodeHandler.GMLANOpCodes.SET_GLOBAL_MEMORY_LENGTH;
            m_instruction.gotoFields[1] = 0x35;
            m_instruction.actionFields[0] = 0x01;
            m_instruction.actionFields[1] = 0x12;
            m_instruction.actionFields[2] = 0x23;
            m_instruction.actionFields[3] = 0x34;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            byte byte00 = (byte)(m_interp.m_opCodeHandler.m_globalMemoryLength & 0x000000FF);
            byte byte01 = (byte)((m_interp.m_opCodeHandler.m_globalMemoryLength & 0x0000FF00) >> 8);
            byte byte02 = (byte)((m_interp.m_opCodeHandler.m_globalMemoryLength & 0x00FF0000) >> 16);
            byte byte03 = (byte)((m_interp.m_opCodeHandler.m_globalMemoryLength & 0xFF000000) >> 24);
            bool match = (byte00 == 0x23) && (byte01 == 0x12) && (byte02 == 0x01) && (byte03 == 0x34);
            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession || !match)
            {
                success = false;
            }
            return success;
        }

        public bool TestSetGlobalHeaderLength()
        {
            bool success = true;
            byte gotoByte = 0x00;
            InitInstruction();
            m_instruction.opCode = (byte)OpCodeHandler.GMLANOpCodes.SET_GLOBAL_HEADER_LENGTH;
            m_instruction.gotoFields[1] = 0x35;
            m_instruction.actionFields[0] = 0x01;
            m_instruction.actionFields[1] = 0x12;
            m_instruction.actionFields[2] = 0x23;
            m_instruction.actionFields[3] = 0x34;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            byte byte00 = (byte)(m_interp.m_opCodeHandler.m_globalHeaderLength & 0x000000FF);
            byte byte01 = (byte)((m_interp.m_opCodeHandler.m_globalHeaderLength & 0x0000FF00) >> 8);
            byte byte02 = (byte)((m_interp.m_opCodeHandler.m_globalHeaderLength & 0x00FF0000) >> 16);
            byte byte03 = (byte)((m_interp.m_opCodeHandler.m_globalHeaderLength & 0xFF000000) >> 24);
            bool match = (byte00 == 0x23) && (byte01 == 0x12) && (byte02 == 0x01) && (byte03 == 0x34);
            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession || !match)
            {
                success = false;
            }
            return success;
        }

        public bool TestOverrideUtilityFileMessageLengthField()
        {
            bool success = true;
            byte gotoByte = 0x00;
            InitInstruction();
            m_instruction.opCode = (byte)OpCodeHandler.GMLANOpCodes.OVERRIDE_THE_UTILITY_FILE_MESSAGE_LENGTH_FIELD;
            m_instruction.gotoFields[1] = 0x35;
            m_instruction.actionFields[0] = 0x01;
            m_instruction.actionFields[1] = 0x12;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            byte byte00 = (byte)(m_interp.m_opCodeHandler.m_header.effectiveDataBytesPerMessage & 0x000000FF);
            byte byte01 = (byte)((m_interp.m_opCodeHandler.m_header.effectiveDataBytesPerMessage & 0x0000FF00) >> 8);

            bool match = (byte00 == 0x12) && (byte01 == 0x01);
            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession || !match)
            {
                success = false;
            }
            return success;
        }
        public bool TestNoOpOpCode()
        {
            bool success = true;
            byte gotoByte = 0x00;
            InitInstruction();
            m_instruction.opCode = (byte)OpCodeHandler.GMLANOpCodes.NO_OPERATION_OP_CODE;
            m_instruction.gotoFields[1] = 0x35;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            //increments step by 1
            if (gotoByte != 0x02 || m_interp.m_opCodeHandler.m_stopProgrammingSession)
            {
                success = false;
            }
            return success;
        }
//Skipping continuation goto

        public bool TestSetAndDecrementCounter()
        {
            bool success = true;
            byte gotoByte = 0x00;
            InitInstruction();
            m_instruction.opCode = (byte)OpCodeHandler.GMLANOpCodes.SET_AND_DECREMENT_COUNTER;
            m_instruction.gotoFields[1] = 0x35;
            m_instruction.gotoFields[3] = 0x85;
            m_instruction.actionFields[0] = 0x02;
            //loop limit
            m_instruction.actionFields[1] = 0x04;

            //Test resetting loop limit
            m_interp.m_opCodeHandler.m_counters[02] = 0xFF;

            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);

            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession || 
                m_interp.m_opCodeHandler.m_counters[02] != 0x03)
            {
                success = false;
            }

            //Test counter expired
            m_interp.m_opCodeHandler.m_counters[02] = 0x01;

            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);

            if (gotoByte != 0x85 || m_interp.m_opCodeHandler.m_stopProgrammingSession ||
                m_interp.m_opCodeHandler.m_counters[02] != 0x00)
            {
                success = false;
            }

            //Test counter decrement normal
            m_interp.m_opCodeHandler.m_counters[02] = 0x02;

            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);

            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession ||
                m_interp.m_opCodeHandler.m_counters[02] != 0x01)
            {
                success = false;
            }

            return success;
        }


        public bool TestDelayForXXSeconds()
        {
            bool success = true;
            byte gotoByte = 0x00;
            InitInstruction();
            m_instruction.opCode = (byte)OpCodeHandler.GMLANOpCodes.DELAY_FOR_XX_SECONDS;
            m_instruction.gotoFields[1] = 0x35;
            m_instruction.gotoFields[3] = 0x85;
            //numb delay sec / min
            m_instruction.actionFields[0] = 0x01;
            m_instruction.actionFields[1] = 0x00;
            //should stay as sec since GMLAN - removed in new manual all adhere to min / sec based on ac3
            m_instruction.actionFields[3] = 0x00;
            m_interp.m_opCodeHandler.m_header.interpType = Interpreter.InterpreterType.CLASS_2;

            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);

            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession)
            {
                success = false;
            }
            m_interp.m_opCodeHandler.m_header.interpType = Interpreter.InterpreterType.GMLAN;
            return success;
        }

        public bool TestResetCounter()
        {
            bool success = true;
            byte gotoByte = 0x00;
            InitInstruction();
            m_instruction.opCode = (byte)OpCodeHandler.GMLANOpCodes.RESET_COUNTER;
            m_instruction.gotoFields[1] = 0x35;
            m_instruction.gotoFields[3] = 0x85;

            //test reset only counter 02
            m_instruction.actionFields[0] = 0x02;
            m_interp.m_opCodeHandler.m_counters[02] = 0xaa;
            m_interp.m_opCodeHandler.m_counters[03] = 0xab;
            m_interp.m_opCodeHandler.m_counters[04] = 0xac;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession ||
                m_interp.m_opCodeHandler.m_counters[02] != 0xFF || m_interp.m_opCodeHandler.m_counters[03] == 0xFF)
            {
                success = false;
            }
            
            //test reset all counters
            m_instruction.actionFields[0] = 0xFF; //FF indicates reset all counters
            m_interp.m_opCodeHandler.m_counters[02] = 0xaa;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession ||
                m_interp.m_opCodeHandler.m_counters[02] != 0xFF || m_interp.m_opCodeHandler.m_counters[03] != 0xFF ||
                m_interp.m_opCodeHandler.m_counters[04] != 0xFF)
            {
                success = false;
            }


            return success;
        }

        public bool TestEndWithSuccess()
        {
            bool success = true;
            byte gotoByte = 0x00;
            InitInstruction();
            m_instruction.opCode = (byte)OpCodeHandler.GMLANOpCodes.END_WITH_SUCCESS;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            if (gotoByte != 0xFF || !m_interp.m_opCodeHandler.m_stopProgrammingSession || 
                !m_interp.m_opCodeHandler.m_currentOpCodeResult.m_result)
            {
                success = false;
            }
            return success;
        }

//GMLAN Op code tests
        public bool TestSetupGlobalAddressBytes()
        {
            bool success = true;
            byte gotoByte = 0x00;
            InitInstruction();
            m_instruction.opCode = (byte)OpCodeHandler.GMLANOpCodes.SETUP_GLOBAL_VARIABLES;
            m_instruction.gotoFields[1] = 0x35;
            m_instruction.gotoFields[3] = 0x85;
            m_instruction.actionFields[0] = 0xf2;
            m_instruction.actionFields[1] = 0xd3;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession ||
                m_interp.m_opCodeHandler.m_globalSourceAddress != 0xd3 || 
                m_interp.m_opCodeHandler.m_globalTargetAddress != 0xf2)
            {
                success = false;
            }
            //test FE failure case
            m_instruction.actionFields[0] = 0xFE;
            m_instruction.actionFields[1] = 0xd4;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);
            //if we did not stop or we set global target or source addresses fail test
            if (gotoByte != 0xFF || !m_interp.m_opCodeHandler.m_stopProgrammingSession ||
                m_interp.m_opCodeHandler.m_globalSourceAddress == 0xd4 ||
                m_interp.m_opCodeHandler.m_globalTargetAddress == 0xFE)
            {
                success = false;
            }
            return success;
        }

        public bool TestGMLANResponseProcessing()
        {
            byte gotoByte = 0x00;
            bool success = true;
            List<byte> effectiveGotoFields = new List<byte>();
            List<byte> data = new List<byte>();
            //Test no goto fields
            gotoByte = m_interp.m_opCodeHandler.GMLANResponseProcessing(effectiveGotoFields, data);
            if (gotoByte != 0xFF || !m_interp.m_opCodeHandler.m_stopProgrammingSession)
            {
                success = false;
            }

            
            //Test Null Data
            effectiveGotoFields.Add(0x11);
            data = null;
            gotoByte = m_interp.m_opCodeHandler.GMLANResponseProcessing(effectiveGotoFields, data);
            if (gotoByte != 0xFF || !m_interp.m_opCodeHandler.m_stopProgrammingSession)
            {
                success = false;
            }


            //Test No data
            data = new List<byte>();            
            effectiveGotoFields.Add(0x12);
            effectiveGotoFields.Add(0xFD);
            effectiveGotoFields.Add(0x35);
            m_interp.m_opCodeHandler.m_stopProgrammingSession = false;

            gotoByte = m_interp.m_opCodeHandler.GMLANResponseProcessing(effectiveGotoFields, data);
            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession)
            {
                success = false;
            }

            //Test negative response non special case
            data.Add(0x7F);
            data.Add(0x10);
            data.Add(0x33);
            effectiveGotoFields.Clear();
            effectiveGotoFields.Add(0x11);
            effectiveGotoFields.Add(0x12);
            effectiveGotoFields.Add(0x33);
            effectiveGotoFields.Add(0x35);

            gotoByte = m_interp.m_opCodeHandler.GMLANResponseProcessing(effectiveGotoFields, data);
            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession)
            {
                success = false;
            }


            //Test negative response special case
            data.Clear();
            data.Add(0x7F);
            data.Add(0x10);
            data.Add(0x50);
            effectiveGotoFields.Clear();
            effectiveGotoFields.Add(0x50);
            effectiveGotoFields.Add(0x12);
            effectiveGotoFields.Add(0x50);
            effectiveGotoFields.Add(0x35);

            gotoByte = m_interp.m_opCodeHandler.GMLANResponseProcessing(effectiveGotoFields, data);
            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession)
            {
                success = false;
            }


            //Test positive response
            data.Clear();
            data.Add(0x50);
            data.Add(0x00);
            effectiveGotoFields.Clear();
            effectiveGotoFields.Add(0x11);
            effectiveGotoFields.Add(0x12);
            effectiveGotoFields.Add(0x50);
            effectiveGotoFields.Add(0x35);

            gotoByte = m_interp.m_opCodeHandler.GMLANResponseProcessing(effectiveGotoFields, data);
            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession)
            {
                success = false;
            }


            //Test no goto match ff default
            data.Clear();
            data.Add(0x50);
            data.Add(0x00);
            effectiveGotoFields.Clear();
            effectiveGotoFields.Add(0x11);
            effectiveGotoFields.Add(0x12);
            effectiveGotoFields.Add(0x51);
            effectiveGotoFields.Add(0x85);
            effectiveGotoFields.Add(0xFF);
            effectiveGotoFields.Add(0x35);

            gotoByte = m_interp.m_opCodeHandler.GMLANResponseProcessing(effectiveGotoFields, data);
            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_stopProgrammingSession)
            {
                success = false;
            }

            return success;
        }

        public bool TestAddGotoFields()
        {
            bool success = true;
            List<byte> effectiveGotoBytes = new List<byte>();

            //set m_instructions to mock list of instructions
            InitInstruction();
            m_interp.m_interpreterInstructions.Add(m_instruction);
            InitInstruction();
            m_instruction.step = 0x02;
            m_interp.m_interpreterInstructions.Add(m_instruction);
            InitInstruction();
            m_instruction.step = 0x03;
            m_interp.m_interpreterInstructions.Add(m_instruction);
            
            //Test Only 1 list of gotos
            m_interp.m_opCodeHandler.AddGotoFields(ref effectiveGotoBytes, m_interp.m_interpreterInstructions[0]);
            if (effectiveGotoBytes.Count != 10)
            {
                success = false;
            }


            //Test 3 list of gotos
            m_interp.m_interpreterInstructions.Clear();
            //set m_instructions to mock list of instructions
            InitInstruction();
            m_interp.m_interpreterInstructions.Add(m_instruction);
            InitInstruction();
            m_instruction.step = 0x02;
            m_instruction.opCode = 0xF8;
            m_instruction.gotoFields = new byte[10] { 0xaa, 0xaa, 0xaa, 0xaa, 0xaa, 0xaa, 0xaa, 0xaa, 0xaa, 0xaa };
            m_interp.m_interpreterInstructions.Add(m_instruction);
            InitInstruction();
            m_instruction.step = 0x03;
            m_instruction.opCode = 0xF8;
            m_instruction.gotoFields = new byte[10] { 0xbb, 0xbb, 0xbb, 0xbb, 0xbb, 0xbb, 0xbb, 0xbb, 0xbb, 0xbb };
            m_interp.m_interpreterInstructions.Add(m_instruction);

            effectiveGotoBytes.Clear();
            m_interp.m_opCodeHandler.AddGotoFields(ref effectiveGotoBytes, m_interp.m_interpreterInstructions[0]);
            if (effectiveGotoBytes.Count != 30 || effectiveGotoBytes[10] != 0xaa || effectiveGotoBytes[20] != 0xbb)
            {
                success = false;
            }

            return success;

        }


        public bool TestCreateECUMessage()
        {
            bool success = true;            
            m_interp.m_opCodeHandler.m_moduleRequestID = 0x1234;
            m_interp.m_opCodeHandler.m_moduleResponseID = 0xabcd;
            List<byte> txMessage = new List<byte>();
            txMessage.Add(0x12);
            txMessage.Add(0x34);
            txMessage.Add(0x56);
            J2534ChannelLibrary.CcrtJ2534Defs.ECUMessage message = m_interp.m_opCodeHandler.CreateECUMessage(ref txMessage);
            if (message.m_messageFilter.requestID[0] != 0x00 ||
                message.m_messageFilter.requestID[1] != 0x00 ||
                message.m_messageFilter.requestID[2] != 0x12 ||  
                message.m_messageFilter.requestID[3] != 0x34 ||
                message.m_messageFilter.responseID[0] != 0x00 ||
                message.m_messageFilter.responseID[1] != 0x00 ||
                message.m_messageFilter.responseID[2] != 0xab || 
                message.m_messageFilter.responseID[3] != 0xcd ||
                txMessage[0] != message.m_txMessage[0] ||
                txMessage[1] != message.m_txMessage[1] ||
                txMessage[2] != message.m_txMessage[2])
            {
                success = false;
            }
            
            return success;
        }

        public bool TestSetupGlobalVariables()
        {
            bool success = true;
            byte gotoByte = 0x00;
            InitInstruction();
            m_instruction.opCode = (byte)OpCodeHandler.GMLANOpCodes.SETUP_GLOBAL_VARIABLES; 
            m_instruction.actionFields[0] = 0xab;
            m_instruction.actionFields[1] = 0xcd;
            m_instruction.gotoFields[1] = 0x35;
            gotoByte = m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);

            if (gotoByte != 0x35 || m_interp.m_opCodeHandler.m_globalTargetAddress != 0xab ||
                m_interp.m_opCodeHandler.m_globalSourceAddress != 0xcd)
            {
                success = false;
            }
            ;
            return success;
        }
        /*
            TestInitiateDiagnosticOperation - nothing to test
         
         */

        /*
            TestReadDataByIdentifier - nothing to test
         
        */

        public bool TestStoreDataByIdentifier()
        {//used by opcodes 1a and 22
            bool success = true;
            InitInstruction();
            m_instruction.actionFields[1] = 0x02;

            byte[] dataArray = new byte[OpCodeHandler.BYTE_STORAGE_MAX+2];
            for (int x = 0; x < OpCodeHandler.BYTE_STORAGE_MAX+2; x++)
            {
                dataArray[x] = Convert.ToByte(((x + 1) % 0xFF));
            }
            List<byte> data = new List<byte>();
            data.AddRange(dataArray);

            //test max storage byte array full
            m_instruction.actionFields[3] = 0x00;
            m_interp.m_opCodeHandler.StoreDataByIdentifier(m_instruction, m_instruction.actionFields[1], 2, ref data);
            byte[] storedData = new byte[OpCodeHandler.BYTE_STORAGE_MAX];
            storedData = m_interp.m_opCodeHandler.GetBytesFromID(0x02);
            bool dataGood = true;
            for(int x = 0; x < storedData.Length; x++)
            {
                if(storedData[x] != dataArray[x+2])
                {
                    dataGood = false;
                    break;
                }
            }
            if (!dataGood)
            {
                success = false;
            }
            //test 2 byte array
            m_instruction.actionFields[3] = 0x01;
            m_interp.m_opCodeHandler.StoreDataByIdentifier(m_instruction, m_instruction.actionFields[1], 2, ref data);
            storedData = m_interp.m_opCodeHandler.GetBytesFromIDTwoByte(0x02);
            dataGood = true;
            for (int x = 0; x < storedData.Length; x++)
            {
                if (storedData[x] != data[x+2])
                {
                    dataGood = false;
                    break;
                }
            }
            if (!dataGood)
            {
                success = false;
            }
            return success;
        }

        /*
            TestSecurityAccess - nothing to test
         
        */

        /*
            TestRequestDownload - nothing to test
         
        */
        public bool TestCreateRequestDownloadMessage()
        {
            bool success = true;
            InitInstruction();
            List<byte> txMessage = new List<byte>();

            return success;
        }

        public bool TestPCMBlockTransferToRAM()
        {
            bool success = true;
            InitInstruction();
            List<J2534ChannelLibrary.CcrtJ2534Defs.ECUMessage> ecuMessages = new List<J2534ChannelLibrary.CcrtJ2534Defs.ECUMessage>();

            //load calibration files
            string fullFileName = "C:\\FlashStation\\CalFiles\\12631684.pti";
            //string fullFileName = "C:\\Documents and Settings\\jsemann\\Desktop\\Isuzu Cals\\TCM\\24242068.pti";
            //empty list of file names for testing

            m_interp.m_calibrationFileNames.Add("C:\\FlashStation\\CalFiles\\12643254.pti");
            m_interp.m_calibrationFileNames.Add("C:\\FlashStation\\CalFiles\\12630292z0.pti");
            m_interp.m_calibrationFileNames.Add("C:\\FlashStation\\CalFiles\\12640754z0.pti");
            m_interp.m_calibrationFileNames.Add("C:\\FlashStation\\CalFiles\\12630294z0.pti");
            //4 could also be this m_interp.m_calibrationFileNames.Add("C:\\Documents and Settings\\jsemann\\Desktop\\Isuzu Cals\\ECM\\12630293z0.pti");
            m_interp.m_calibrationFileNames.Add("C:\\FlashStation\\CalFiles\\12630290z0.pti");
            // 5 could also be this m_interp.m_calibrationFileNames.Add("C:\\Documents and Settings\\jsemann\\Desktop\\Isuzu Cals\\ECM\\12630289z0.pti");
            m_interp.m_calibrationFileNames.Add("C:\\FlashStation\\CalFiles\\12630287z0.pti");
            //m_interp.m_calibrationFileNames.Add("C:\\Documents and Settings\\jsemann\\Desktop\\Isuzu Cals\\ECM\\12625892.pti");
            //m_interp.m_calibrationFileNames.Add("C:\\Documents and Settings\\jsemann\\Desktop\\Isuzu Cals\\ECM\\12630288z0.pti");
            m_interp.m_headerOffset = 0x64;
            m_interp.openUtilityFile(fullFileName);
            m_interp.m_opCodeHandler.m_header = m_interp.m_header;


            //Module 2
            //set up global address length
            m_instruction.opCode = (byte)OpCodeHandler.GMLANOpCodes.SET_GLOBAL_HEADER_LENGTH;
            m_instruction.actionFields[0] = 0x00;
            m_instruction.actionFields[1] = 0x00;
            m_instruction.actionFields[2] = 0x00;
            m_instruction.actionFields[3] = 0x00;
            m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);

            //setup instruction
            m_instruction.actionFields[0] = 0x02;
            m_instruction.actionFields[1] = 0x00;
            m_instruction.actionFields[2] = 0x01;
            m_instruction.actionFields[3] = 0x14;

            ecuMessages.Clear();
            m_interp.m_opCodeHandler.CreateBlockTransferMessages(m_instruction, ref ecuMessages);
            //check message size and start / end bytes (message size can be retrieved from CAN bus log in some cases)
            if (ecuMessages[0].m_txMessage.Count != 0x0FFE ||
                ecuMessages[1].m_txMessage.Count != 0x021E ||
                ecuMessages[0].m_txMessage[0] != 0x36 || 
                ecuMessages[1].m_txMessage[0] != 0x36 || 
                ecuMessages[0].m_txMessage[1] != 0x00 || 
                ecuMessages[1].m_txMessage[1] != 0x00 || 
                ecuMessages[0].m_txMessage[2] != 0x00 || 
                ecuMessages[1].m_txMessage[2] != 0x00 || 
                ecuMessages[0].m_txMessage[3] != 0x3F || 
                ecuMessages[1].m_txMessage[3] != 0x3F || 
                ecuMessages[0].m_txMessage[4] != 0x90 || 
                ecuMessages[1].m_txMessage[4] != 0x90 || 
                ecuMessages[0].m_txMessage[5] != 0x90 || 
                ecuMessages[1].m_txMessage[5] != 0x90 ||                 
                ecuMessages[0].m_txMessage[6] != 0x16 || 
                ecuMessages[1].m_txMessage[6] != 0x00 ||                 
                ecuMessages[0].m_txMessage[7] != 0x11 || 
                ecuMessages[1].m_txMessage[7] != 0x50 ||                 
                ecuMessages[0].m_txMessage[8] != 0x81 || 
                ecuMessages[1].m_txMessage[8] != 0x00 ||  
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 4] != 0x00 || 
                ecuMessages[1].m_txMessage[ecuMessages[1].m_txMessage.Count - 4] != 0xFF ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 3] != 0x50 || 
                ecuMessages[1].m_txMessage[ecuMessages[1].m_txMessage.Count - 3] != 0xFF ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 2] != 0x00 || 
                ecuMessages[1].m_txMessage[ecuMessages[1].m_txMessage.Count - 2] != 0xFF ||               
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 1] != 0x50 || 
                ecuMessages[1].m_txMessage[ecuMessages[1].m_txMessage.Count - 1] != 0xFF               
                )
            {
                success = false;
            }

            //Module 3
            //set up global address length
            m_instruction.opCode = (byte)OpCodeHandler.GMLANOpCodes.SET_GLOBAL_HEADER_LENGTH;
            m_instruction.actionFields[0] = 0x00;
            m_instruction.actionFields[1] = 0x00;
            m_instruction.actionFields[2] = 0x00;
            m_instruction.actionFields[3] = 0x00;
            m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);

            //setup instruction
            m_instruction.actionFields[0] = 0x03;
            m_instruction.actionFields[1] = 0x00;
            m_instruction.actionFields[2] = 0x01;
            m_instruction.actionFields[3] = 0x14;

            ecuMessages.Clear();
            m_interp.m_opCodeHandler.CreateBlockTransferMessages(m_instruction, ref ecuMessages);
            //check message size and start / end bytes (message size can be retrieved from CAN bus log in some cases)
            if (ecuMessages[0].m_txMessage.Count != 0x0FFE ||
                ecuMessages[1].m_txMessage.Count != 0x0FFE ||
                ecuMessages[2].m_txMessage.Count != 0x01C6 ||
                ecuMessages[0].m_txMessage[0] != 0x36 ||
                ecuMessages[1].m_txMessage[0] != 0x36 ||
                ecuMessages[2].m_txMessage[0] != 0x36 ||
                ecuMessages[0].m_txMessage[1] != 0x00 ||
                ecuMessages[1].m_txMessage[1] != 0x00 ||
                ecuMessages[2].m_txMessage[1] != 0x00 ||
                ecuMessages[0].m_txMessage[2] != 0x00 ||
                ecuMessages[1].m_txMessage[2] != 0x00 ||
                ecuMessages[2].m_txMessage[2] != 0x00 ||
                ecuMessages[0].m_txMessage[3] != 0x3F ||
                ecuMessages[1].m_txMessage[3] != 0x3F ||
                ecuMessages[2].m_txMessage[3] != 0x3F ||
                ecuMessages[0].m_txMessage[4] != 0x90 ||
                ecuMessages[1].m_txMessage[4] != 0x90 ||
                ecuMessages[2].m_txMessage[4] != 0x90 ||
                ecuMessages[0].m_txMessage[5] != 0x90 ||
                ecuMessages[1].m_txMessage[5] != 0x90 ||
                ecuMessages[2].m_txMessage[5] != 0x90 ||
                ecuMessages[0].m_txMessage[6] != 0xC9 ||
                ecuMessages[1].m_txMessage[6] != 0xF0 ||
                ecuMessages[2].m_txMessage[6] != 0x1C ||
                ecuMessages[0].m_txMessage[7] != 0xEF ||
                ecuMessages[1].m_txMessage[7] != 0x38 ||
                ecuMessages[2].m_txMessage[7] != 0x4C ||
                ecuMessages[0].m_txMessage[8] != 0x82 ||
                ecuMessages[1].m_txMessage[8] != 0xFB ||
                ecuMessages[2].m_txMessage[8] != 0x1D ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 4] != 0xDA ||
                ecuMessages[1].m_txMessage[ecuMessages[1].m_txMessage.Count - 4] != 0x19 ||
                ecuMessages[2].m_txMessage[ecuMessages[2].m_txMessage.Count - 4] != 0xFF ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 3] != 0x6F ||
                ecuMessages[1].m_txMessage[ecuMessages[1].m_txMessage.Count - 3] != 0x0D ||
                ecuMessages[2].m_txMessage[ecuMessages[2].m_txMessage.Count - 3] != 0xFF ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 2] != 0xE5 ||
                ecuMessages[1].m_txMessage[ecuMessages[1].m_txMessage.Count - 2] != 0x1A ||
                ecuMessages[2].m_txMessage[ecuMessages[2].m_txMessage.Count - 2] != 0xFF ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 1] != 0x2E ||
                ecuMessages[1].m_txMessage[ecuMessages[1].m_txMessage.Count - 1] != 0xAD ||
                ecuMessages[2].m_txMessage[ecuMessages[2].m_txMessage.Count - 1] != 0xFF
                )
            {
                success = false;
            }

            //Module 4 (94z0)
            //set up global address length
            m_instruction.opCode = (byte)OpCodeHandler.GMLANOpCodes.SET_GLOBAL_HEADER_LENGTH;
            m_instruction.actionFields[0] = 0x00;
            m_instruction.actionFields[1] = 0x00;
            m_instruction.actionFields[2] = 0x00;
            m_instruction.actionFields[3] = 0x00;
            m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);

            //setup instruction
            m_instruction.actionFields[0] = 0x04;
            m_instruction.actionFields[1] = 0x00;
            m_instruction.actionFields[2] = 0x01;
            m_instruction.actionFields[3] = 0x14;

            ecuMessages.Clear();
            m_interp.m_opCodeHandler.CreateBlockTransferMessages(m_instruction, ref ecuMessages);
            //check message size and start / end bytes (message size can be retrieved from CAN bus log in some cases)
            if (ecuMessages[0].m_txMessage.Count != 0x0366 ||
                ecuMessages[0].m_txMessage[0] != 0x36 ||
                ecuMessages[0].m_txMessage[1] != 0x00 ||
                ecuMessages[0].m_txMessage[2] != 0x00 ||
                ecuMessages[0].m_txMessage[3] != 0x3F ||
                ecuMessages[0].m_txMessage[4] != 0x90 ||
                ecuMessages[0].m_txMessage[5] != 0x90 ||
                ecuMessages[0].m_txMessage[6] != 0xE6 ||
                ecuMessages[0].m_txMessage[7] != 0xA2 ||
                ecuMessages[0].m_txMessage[8] != 0x83 ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 4] != 0xFF ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 3] != 0xFF ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 2] != 0xFF ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 1] != 0xFF
                )
            {
                success = false;
            }


            //Module 5 (90z0)
            //set up global address length
            m_instruction.opCode = (byte)OpCodeHandler.GMLANOpCodes.SET_GLOBAL_HEADER_LENGTH;
            m_instruction.actionFields[0] = 0x00;
            m_instruction.actionFields[1] = 0x00;
            m_instruction.actionFields[2] = 0x00;
            m_instruction.actionFields[3] = 0x00;
            m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);

            //setup instruction
            m_instruction.actionFields[0] = 0x05;
            m_instruction.actionFields[1] = 0x00;
            m_instruction.actionFields[2] = 0x01;
            m_instruction.actionFields[3] = 0x14;

            ecuMessages.Clear();
            m_interp.m_opCodeHandler.CreateBlockTransferMessages(m_instruction, ref ecuMessages);
            //check message size and start / end bytes (message size can be retrieved from CAN bus log in some cases)
            if (ecuMessages[0].m_txMessage.Count != 0x0FFE ||
                ecuMessages[0].m_txMessage[0] != 0x36 ||
                ecuMessages[0].m_txMessage[1] != 0x00 ||
                ecuMessages[0].m_txMessage[2] != 0x00 ||
                ecuMessages[0].m_txMessage[3] != 0x3F ||
                ecuMessages[0].m_txMessage[4] != 0x90 ||
                ecuMessages[0].m_txMessage[5] != 0x90 ||
                ecuMessages[0].m_txMessage[6] != 0x22 ||
                ecuMessages[0].m_txMessage[7] != 0xB6 ||
                ecuMessages[0].m_txMessage[8] != 0x84 ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 4] != 0x07 ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 3] != 0x80 ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 2] != 0x07 ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 1] != 0x80
                )
            {
                success = false;
            }
            if (ecuMessages[1].m_txMessage.Count != 0x0FFE ||
                ecuMessages[1].m_txMessage[0] != 0x36 ||
                ecuMessages[1].m_txMessage[1] != 0x00 ||
                ecuMessages[1].m_txMessage[2] != 0x00 ||
                ecuMessages[1].m_txMessage[3] != 0x3F ||
                ecuMessages[1].m_txMessage[4] != 0x90 ||
                ecuMessages[1].m_txMessage[5] != 0x90 ||
                ecuMessages[1].m_txMessage[6] != 0x07 ||
                ecuMessages[1].m_txMessage[7] != 0x80 ||
                ecuMessages[1].m_txMessage[8] != 0x07 ||
                ecuMessages[1].m_txMessage[ecuMessages[1].m_txMessage.Count - 4] != 0x00 ||
                ecuMessages[1].m_txMessage[ecuMessages[1].m_txMessage.Count - 3] != 0x00 ||
                ecuMessages[1].m_txMessage[ecuMessages[1].m_txMessage.Count - 2] != 0x00 ||
                ecuMessages[1].m_txMessage[ecuMessages[1].m_txMessage.Count - 1] != 0x00
                )
            {
                success = false;
            }
            if (ecuMessages[2].m_txMessage.Count != 0x0FFE ||
                ecuMessages[2].m_txMessage[0] != 0x36 ||
                ecuMessages[2].m_txMessage[1] != 0x00 ||
                ecuMessages[2].m_txMessage[2] != 0x00 ||
                ecuMessages[2].m_txMessage[3] != 0x3F ||
                ecuMessages[2].m_txMessage[4] != 0x90 ||
                ecuMessages[2].m_txMessage[5] != 0x90 ||
                ecuMessages[2].m_txMessage[6] != 0x00 ||
                ecuMessages[2].m_txMessage[7] != 0x00 ||
                ecuMessages[2].m_txMessage[8] != 0x00 ||
                ecuMessages[2].m_txMessage[ecuMessages[2].m_txMessage.Count - 4] != 0x80 ||
                ecuMessages[2].m_txMessage[ecuMessages[2].m_txMessage.Count - 3] != 0x00 ||
                ecuMessages[2].m_txMessage[ecuMessages[2].m_txMessage.Count - 2] != 0x80 ||
                ecuMessages[2].m_txMessage[ecuMessages[2].m_txMessage.Count - 1] != 0x00
                )
            {
                success = false;
            }
            if (ecuMessages[3].m_txMessage.Count != 0x0FFE ||
                ecuMessages[3].m_txMessage[0] != 0x36 ||
                ecuMessages[3].m_txMessage[1] != 0x00 ||
                ecuMessages[3].m_txMessage[2] != 0x00 ||
                ecuMessages[3].m_txMessage[3] != 0x3F ||
                ecuMessages[3].m_txMessage[4] != 0x90 ||
                ecuMessages[3].m_txMessage[5] != 0x90 ||
                ecuMessages[3].m_txMessage[6] != 0x80 ||
                ecuMessages[3].m_txMessage[7] != 0x00 ||
                ecuMessages[3].m_txMessage[8] != 0x80 ||
                ecuMessages[3].m_txMessage[ecuMessages[3].m_txMessage.Count - 4] != 0x00 ||
                ecuMessages[3].m_txMessage[ecuMessages[3].m_txMessage.Count - 3] != 0x00 ||
                ecuMessages[3].m_txMessage[ecuMessages[3].m_txMessage.Count - 2] != 0x00 ||
                ecuMessages[3].m_txMessage[ecuMessages[3].m_txMessage.Count - 1] != 0x00
                )
            {
                success = false;
            }
            if (ecuMessages[4].m_txMessage.Count != 0x0FFE ||
                ecuMessages[4].m_txMessage[0] != 0x36 ||
                ecuMessages[4].m_txMessage[1] != 0x00 ||
                ecuMessages[4].m_txMessage[2] != 0x00 ||
                ecuMessages[4].m_txMessage[3] != 0x3F ||
                ecuMessages[4].m_txMessage[4] != 0x90 ||
                ecuMessages[4].m_txMessage[5] != 0x90 ||
                ecuMessages[4].m_txMessage[6] != 0x3E ||
                ecuMessages[4].m_txMessage[7] != 0x80 ||
                ecuMessages[4].m_txMessage[8] != 0x23 ||
                ecuMessages[4].m_txMessage[ecuMessages[4].m_txMessage.Count - 4] != 0x0A ||
                ecuMessages[4].m_txMessage[ecuMessages[4].m_txMessage.Count - 3] != 0xF0 ||
                ecuMessages[4].m_txMessage[ecuMessages[4].m_txMessage.Count - 2] != 0x0A ||
                ecuMessages[4].m_txMessage[ecuMessages[4].m_txMessage.Count - 1] != 0x28
                )
            {
                success = false;
            }
            if (ecuMessages[5].m_txMessage.Count != 0x0FFE ||
                ecuMessages[5].m_txMessage[0] != 0x36 ||
                ecuMessages[5].m_txMessage[1] != 0x00 ||
                ecuMessages[5].m_txMessage[2] != 0x00 ||
                ecuMessages[5].m_txMessage[3] != 0x3F ||
                ecuMessages[5].m_txMessage[4] != 0x90 ||
                ecuMessages[5].m_txMessage[5] != 0x90 ||
                ecuMessages[5].m_txMessage[6] != 0x07 ||
                ecuMessages[5].m_txMessage[7] != 0x08 ||
                ecuMessages[5].m_txMessage[8] != 0x04 ||
                ecuMessages[5].m_txMessage[ecuMessages[5].m_txMessage.Count - 4] != 0x0A ||
                ecuMessages[5].m_txMessage[ecuMessages[5].m_txMessage.Count - 3] != 0x28 ||
                ecuMessages[5].m_txMessage[ecuMessages[5].m_txMessage.Count - 2] != 0x08 ||
                ecuMessages[5].m_txMessage[ecuMessages[5].m_txMessage.Count - 1] != 0xFC
                )
            {
                success = false;
            }
            if (ecuMessages[6].m_txMessage.Count != 0x0FFE ||
                ecuMessages[6].m_txMessage[0] != 0x36 ||
                ecuMessages[6].m_txMessage[1] != 0x00 ||
                ecuMessages[6].m_txMessage[2] != 0x00 ||
                ecuMessages[6].m_txMessage[3] != 0x3F ||
                ecuMessages[6].m_txMessage[4] != 0x90 ||
                ecuMessages[6].m_txMessage[5] != 0x90 ||
                ecuMessages[6].m_txMessage[6] != 0x06 ||
                ecuMessages[6].m_txMessage[7] != 0xA4 ||
                ecuMessages[6].m_txMessage[8] != 0x04 ||
                ecuMessages[6].m_txMessage[ecuMessages[6].m_txMessage.Count - 4] != 0x00 ||
                ecuMessages[6].m_txMessage[ecuMessages[6].m_txMessage.Count - 3] != 0x00 ||
                ecuMessages[6].m_txMessage[ecuMessages[6].m_txMessage.Count - 2] != 0x00 ||
                ecuMessages[6].m_txMessage[ecuMessages[6].m_txMessage.Count - 1] != 0x00
                )
            {
                success = false;
            }
            if (ecuMessages[7].m_txMessage.Count != 0x0FFE ||
                ecuMessages[7].m_txMessage[0] != 0x36 ||
                ecuMessages[7].m_txMessage[1] != 0x00 ||
                ecuMessages[7].m_txMessage[2] != 0x00 ||
                ecuMessages[7].m_txMessage[3] != 0x3F ||
                ecuMessages[7].m_txMessage[4] != 0x90 ||
                ecuMessages[7].m_txMessage[5] != 0x90 ||
                ecuMessages[7].m_txMessage[6] != 0x00 ||
                ecuMessages[7].m_txMessage[7] != 0x00 ||
                ecuMessages[7].m_txMessage[8] != 0x00 ||
                ecuMessages[7].m_txMessage[ecuMessages[7].m_txMessage.Count - 4] != 0x40 ||
                ecuMessages[7].m_txMessage[ecuMessages[7].m_txMessage.Count - 3] != 0x00 ||
                ecuMessages[7].m_txMessage[ecuMessages[7].m_txMessage.Count - 2] != 0x00 ||
                ecuMessages[7].m_txMessage[ecuMessages[7].m_txMessage.Count - 1] != 0x00
                )
            {
                success = false;
            }
            if (ecuMessages[8].m_txMessage.Count != 0x0FFE ||
                ecuMessages[8].m_txMessage[0] != 0x36 ||
                ecuMessages[8].m_txMessage[1] != 0x00 ||
                ecuMessages[8].m_txMessage[2] != 0x00 ||
                ecuMessages[8].m_txMessage[3] != 0x3F ||
                ecuMessages[8].m_txMessage[4] != 0x90 ||
                ecuMessages[8].m_txMessage[5] != 0x90 ||
                ecuMessages[8].m_txMessage[6] != 0x40 ||
                ecuMessages[8].m_txMessage[7] != 0x00 ||
                ecuMessages[8].m_txMessage[8] != 0x00 ||
                ecuMessages[8].m_txMessage[ecuMessages[8].m_txMessage.Count - 4] != 0x01 ||
                ecuMessages[8].m_txMessage[ecuMessages[8].m_txMessage.Count - 3] != 0x40 ||
                ecuMessages[8].m_txMessage[ecuMessages[8].m_txMessage.Count - 2] != 0x01 ||
                ecuMessages[8].m_txMessage[ecuMessages[8].m_txMessage.Count - 1] != 0x40
                )
            {
                success = false;
            }
            if (ecuMessages[9].m_txMessage.Count != 0x09FE ||
                ecuMessages[9].m_txMessage[0] != 0x36 ||
                ecuMessages[9].m_txMessage[1] != 0x00 ||
                ecuMessages[9].m_txMessage[2] != 0x00 ||
                ecuMessages[9].m_txMessage[3] != 0x3F ||
                ecuMessages[9].m_txMessage[4] != 0x90 ||
                ecuMessages[9].m_txMessage[5] != 0x90 ||
                ecuMessages[9].m_txMessage[6] != 0x01 ||
                ecuMessages[9].m_txMessage[7] != 0x40 ||
                ecuMessages[9].m_txMessage[8] != 0x01 ||
                ecuMessages[9].m_txMessage[ecuMessages[9].m_txMessage.Count - 4] != 0xFF ||
                ecuMessages[9].m_txMessage[ecuMessages[9].m_txMessage.Count - 3] != 0xFF ||
                ecuMessages[9].m_txMessage[ecuMessages[9].m_txMessage.Count - 2] != 0xFF ||
                ecuMessages[9].m_txMessage[ecuMessages[9].m_txMessage.Count - 1] != 0xFF
                )
            {
                success = false;
            }


            //Module 6 VERY LARGE (87z0)
            //set up global address length
            m_instruction.opCode = (byte)OpCodeHandler.GMLANOpCodes.SET_GLOBAL_HEADER_LENGTH;
            m_instruction.actionFields[0] = 0x00;
            m_instruction.actionFields[1] = 0x00;
            m_instruction.actionFields[2] = 0x00;
            m_instruction.actionFields[3] = 0x00;
            m_interp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);

            //setup instruction
            m_instruction.actionFields[0] = 0x06;
            m_instruction.actionFields[1] = 0x00;
            m_instruction.actionFields[2] = 0x01;
            m_instruction.actionFields[3] = 0x14;

            ecuMessages.Clear();
            m_interp.m_opCodeHandler.CreateBlockTransferMessages(m_instruction, ref ecuMessages);
            //check message size and start / end bytes (message size can be retrieved from CAN bus log in some cases)
            if (ecuMessages[0].m_txMessage.Count != 0x0FFE ||
                ecuMessages[0].m_txMessage[0] != 0x36 ||
                ecuMessages[0].m_txMessage[1] != 0x00 ||
                ecuMessages[0].m_txMessage[2] != 0x00 ||
                ecuMessages[0].m_txMessage[3] != 0x3F ||
                ecuMessages[0].m_txMessage[4] != 0x90 ||
                ecuMessages[0].m_txMessage[5] != 0x90 ||
                ecuMessages[0].m_txMessage[6] != 0x68 ||
                ecuMessages[0].m_txMessage[7] != 0xBE ||
                ecuMessages[0].m_txMessage[8] != 0x85 ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 4] != 0xFF ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 3] != 0xFF ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 2] != 0x06 ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 1] != 0x66
                )
            {
                success = false;
            }
            if (ecuMessages[ecuMessages.Count - 1].m_txMessage.Count != 0x00CE ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[0] != 0x36 ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[1] != 0x00 ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[2] != 0x00 ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[3] != 0x3F ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[4] != 0x90 ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[5] != 0x90 ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[6] != 0xFF ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[7] != 0xFF ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[8] != 0xFF ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[ecuMessages[ecuMessages.Count - 1].m_txMessage.Count - 4] != 0xFF ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[ecuMessages[ecuMessages.Count - 1].m_txMessage.Count - 3] != 0xFF ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[ecuMessages[ecuMessages.Count - 1].m_txMessage.Count - 2] != 0xFF ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[ecuMessages[ecuMessages.Count - 1].m_txMessage.Count - 1] != 0xFF
                )
            {
                success = false;
            }


            return success;
        }

        public bool TestTCMBlockTransferToRAM()
        {
            bool success = true;
            InitInstruction();
            //empty list of file names for testing
            List<string> calFileNames = new List<string>();
            calFileNames.Clear();
            calFileNames.Add("C:\\FlashStation\\CalFiles\\24260314ac.pti");
            calFileNames.Add("C:\\FlashStation\\CalFiles\\24259005z0.pti");
            calFileNames.Add("C:\\FlashStation\\CalFiles\\24259006z0.pti");
            calFileNames.Add("C:\\FlashStation\\CalFiles\\24259007z0.pti");
            List<J2534ChannelLibrary.CcrtJ2534Defs.ECUMessage> ecuMessages = new List<J2534ChannelLibrary.CcrtJ2534Defs.ECUMessage>();
            Interpreter tcmInterp = new Interpreter(m_vehicleCommInterface, ref calFileNames, m_deviceName, m_channelName);
            //load calibration files
            string fullFileName = "C:\\FlashStation\\CalFiles\\24242068.pti";


            tcmInterp.m_headerOffset = 0x64;
            tcmInterp.openUtilityFile(fullFileName);
            tcmInterp.m_opCodeHandler.m_header = m_interp.m_header;

            //Module 2 VERY LARGE
            //set up global address length
            m_instruction.opCode = (byte)OpCodeHandler.GMLANOpCodes.SET_GLOBAL_HEADER_LENGTH;
            m_instruction.actionFields[0] = 0x00;
            m_instruction.actionFields[1] = 0x00;
            m_instruction.actionFields[2] = 0x00;
            m_instruction.actionFields[3] = 0x00;
            tcmInterp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);

            //setup instruction
            m_instruction.actionFields[0] = 0x02;
            m_instruction.actionFields[1] = 0x00;
            m_instruction.actionFields[2] = 0x01;
            m_instruction.actionFields[3] = 0x14;

            ecuMessages.Clear();
            tcmInterp.m_opCodeHandler.CreateBlockTransferMessages(m_instruction, ref ecuMessages);
            //check message size and start / end bytes (message size can be retrieved from CAN bus log in some cases)
            if (ecuMessages[0].m_txMessage.Count != 0x0FF6 ||
                ecuMessages[0].m_txMessage[0] != 0x36 ||
                ecuMessages[0].m_txMessage[1] != 0x00 ||
                ecuMessages[0].m_txMessage[2] != 0x00 ||
                ecuMessages[0].m_txMessage[3] != 0x3F ||
                ecuMessages[0].m_txMessage[4] != 0xC0 ||
                ecuMessages[0].m_txMessage[5] != 0x00 ||
                ecuMessages[0].m_txMessage[6] != 0xAE ||
                ecuMessages[0].m_txMessage[7] != 0xC6 ||
                ecuMessages[0].m_txMessage[8] != 0x81 ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 4] != 0x00 ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 3] != 0x09 ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 2] != 0x00 ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 1] != 0x00
                )
            {
                success = false;
            }
            if (ecuMessages[ecuMessages.Count - 1].m_txMessage.Count != 0x0F1E ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[0] != 0x36 ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[1] != 0x00 ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[2] != 0x00 ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[3] != 0x3F ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[4] != 0xC0 ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[5] != 0x00 ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[6] != 0xFF ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[7] != 0xFF ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[8] != 0xFF ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[ecuMessages[ecuMessages.Count - 1].m_txMessage.Count - 4] != 0xFF ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[ecuMessages[ecuMessages.Count - 1].m_txMessage.Count - 3] != 0xFF ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[ecuMessages[ecuMessages.Count - 1].m_txMessage.Count - 2] != 0xFF ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[ecuMessages[ecuMessages.Count - 1].m_txMessage.Count - 1] != 0xFF
                )
            {
                success = false;
            }

            //Module 3
            //set up global address length
            m_instruction.opCode = (byte)OpCodeHandler.GMLANOpCodes.SET_GLOBAL_HEADER_LENGTH;
            m_instruction.actionFields[0] = 0x00;
            m_instruction.actionFields[1] = 0x00;
            m_instruction.actionFields[2] = 0x00;
            m_instruction.actionFields[3] = 0x00;
            tcmInterp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);

            //setup instruction
            m_instruction.actionFields[0] = 0x03;
            m_instruction.actionFields[1] = 0x00;
            m_instruction.actionFields[2] = 0x01;
            m_instruction.actionFields[3] = 0x14;

            ecuMessages.Clear();
            tcmInterp.m_opCodeHandler.CreateBlockTransferMessages(m_instruction, ref ecuMessages);
            //check message size and start / end bytes (message size can be retrieved from CAN bus log in some cases)
            if (ecuMessages[0].m_txMessage.Count != 0x0FF6 ||
                ecuMessages[0].m_txMessage[0] != 0x36 ||
                ecuMessages[0].m_txMessage[1] != 0x00 ||
                ecuMessages[0].m_txMessage[2] != 0x00 ||
                ecuMessages[0].m_txMessage[3] != 0x3F ||
                ecuMessages[0].m_txMessage[4] != 0xC0 ||
                ecuMessages[0].m_txMessage[5] != 0x00 ||
                ecuMessages[0].m_txMessage[6] != 0xB3 ||
                ecuMessages[0].m_txMessage[7] != 0x76 ||
                ecuMessages[0].m_txMessage[8] != 0x82 ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 4] != 0x03 ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 3] != 0xFF ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 2] != 0xFF ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 1] != 0xFF
                )
            {
                success = false;
            }
            if (ecuMessages[ecuMessages.Count - 1].m_txMessage.Count != 0x0026 ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[0] != 0x36 ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[1] != 0x00 ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[2] != 0x00 ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[3] != 0x3F ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[4] != 0xC0 ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[5] != 0x00 ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[6] != 0xFF ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[7] != 0xFF ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[8] != 0xFF ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[ecuMessages[ecuMessages.Count - 1].m_txMessage.Count - 4] != 0xFF ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[ecuMessages[ecuMessages.Count - 1].m_txMessage.Count - 3] != 0xFF ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[ecuMessages[ecuMessages.Count - 1].m_txMessage.Count - 2] != 0xFF ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[ecuMessages[ecuMessages.Count - 1].m_txMessage.Count - 1] != 0xFF
                )
            {
                success = false;
            }

            //Module 4
            //set up global address length
            m_instruction.opCode = (byte)OpCodeHandler.GMLANOpCodes.SET_GLOBAL_HEADER_LENGTH;
            m_instruction.actionFields[0] = 0x00;
            m_instruction.actionFields[1] = 0x00;
            m_instruction.actionFields[2] = 0x00;
            m_instruction.actionFields[3] = 0x00;
            tcmInterp.m_opCodeHandler.ProcessGMLANOpCode(m_instruction);

            //setup instruction
            m_instruction.actionFields[0] = 0x04;
            m_instruction.actionFields[1] = 0x00;
            m_instruction.actionFields[2] = 0x01;
            m_instruction.actionFields[3] = 0x14;

            ecuMessages.Clear();
            tcmInterp.m_opCodeHandler.CreateBlockTransferMessages(m_instruction, ref ecuMessages);
            //check message size and start / end bytes (message size can be retrieved from CAN bus log in some cases)
            if (ecuMessages[0].m_txMessage.Count != 0x0FF6 ||
                ecuMessages[0].m_txMessage[0] != 0x36 ||
                ecuMessages[0].m_txMessage[1] != 0x00 ||
                ecuMessages[0].m_txMessage[2] != 0x00 ||
                ecuMessages[0].m_txMessage[3] != 0x3F ||
                ecuMessages[0].m_txMessage[4] != 0xC0 ||
                ecuMessages[0].m_txMessage[5] != 0x00 ||
                ecuMessages[0].m_txMessage[6] != 0xF0 ||
                ecuMessages[0].m_txMessage[7] != 0xCC ||
                ecuMessages[0].m_txMessage[8] != 0x83 ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 4] != 0xFF ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 3] != 0xFF ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 2] != 0xFF ||
                ecuMessages[0].m_txMessage[ecuMessages[0].m_txMessage.Count - 1] != 0xFF
                )
            {
                success = false;
            }
            if (ecuMessages[ecuMessages.Count - 1].m_txMessage.Count != 0x04BE ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[0] != 0x36 ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[1] != 0x00 ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[2] != 0x00 ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[3] != 0x3F ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[4] != 0xC0 ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[5] != 0x00 ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[6] != 0xFF ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[7] != 0xFF ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[8] != 0xFF ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[ecuMessages[ecuMessages.Count - 1].m_txMessage.Count - 4] != 0xFF ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[ecuMessages[ecuMessages.Count - 1].m_txMessage.Count - 3] != 0xFF ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[ecuMessages[ecuMessages.Count - 1].m_txMessage.Count - 2] != 0xFF ||
                ecuMessages[ecuMessages.Count - 1].m_txMessage[ecuMessages[ecuMessages.Count - 1].m_txMessage.Count - 1] != 0xFF
                )
            {
                success = false;
            }
            ecuMessages.Clear();
            return success;

        }




        private void m_startTestButton_Click(object sender, EventArgs e)
        {
            RunCommonOpCodeTests();
            RunGMLANOpCodeTests();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            while (true)
            {
                TestTCMBlockTransferToRAM();
                System.Threading.Thread.Sleep(250);
            }
        }

    }
}
