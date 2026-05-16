using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using UtilityFileInterpreter;
using LoggingInterface;

namespace UtilityFileInterpreterViewer
{
    public partial class UtilityFileDisplay : Form
    {
        public VehicleCommServer.IVehicleCommServerInterface m_vehicleCommInterface;
        public UtilityFileDisplay()
        {
            InitializeComponent();
            m_vehicleCommInterface = new VehicleCommServer.VehicleCommServerInterface();
            
            int headerOffset = 0x64;
            //hard coded
            string fullFileName = "C:\\Documents and Settings\\jsemann\\Desktop\\Isuzu Cals\\ECM\\12631684.pti";
            //string fullFileName = "C:\\Documents and Settings\\jsemann\\Desktop\\Isuzu Cals\\TCM\\24242068.pti";
            //empty list of file names for testing
            List<string> calibrationFileNames = new List<string>();
            //calibrationFileNames.Add("C:\\Documents and Settings\\jsemann\\Desktop\\Isuzu Cals\\ECM\\12643254.pti");
            //calibrationFileNames.Add("C:\\Documents and Settings\\jsemann\\Desktop\\Isuzu Cals\\ECM\\12630292z0.pti");
            //calibrationFileNames.Add("C:\\Documents and Settings\\jsemann\\Desktop\\Isuzu Cals\\ECM\\12640754z0.pti");
            //calibrationFileNames.Add("C:\\Documents and Settings\\jsemann\\Desktop\\Isuzu Cals\\ECM\\12630293z0.pti");
            //calibrationFileNames.Add("C:\\Documents and Settings\\jsemann\\Desktop\\Isuzu Cals\\ECM\\12630289z0.pti");
            //calibrationFileNames.Add("C:\\Documents and Settings\\jsemann\\Desktop\\Isuzu Cals\\ECM\\12630287z0.pti");
            //calibrationFileNames.Add("C:\\Documents and Settings\\jsemann\\Desktop\\Isuzu Cals\\ECM\\12625892.pti");
            //calibrationFileNames.Add("C:\\Documents and Settings\\jsemann\\Desktop\\Isuzu Cals\\ECM\\12630288z0.pti");
            
            
            //calibrationFileNames.Add("C:\\Documents and Settings\\jsemann\\Desktop\\Isuzu Cals\\TCM\\24260314ac.pti");
            //calibrationFileNames.Add("C:\\Documents and Settings\\jsemann\\Desktop\\Isuzu Cals\\TCM\\24259005z0.pti");
            //calibrationFileNames.Add("C:\\Documents and Settings\\jsemann\\Desktop\\Isuzu Cals\\TCM\\24259006z0.pti");
            //calibrationFileNames.Add("C:\\Documents and Settings\\jsemann\\Desktop\\Isuzu Cals\\TCM\\24259007z0.pti");
            Interpreter interp = new Interpreter(m_vehicleCommInterface, ref calibrationFileNames, "CarDAQ PLUS", "GMLAN");
            interp.m_headerOffset = headerOffset;
            interp.openUtilityFile(fullFileName);
            m_apiHeaderDataGridView.Rows.Add();
            m_apiHeaderDataGridView.Rows[0].Cells[0].Value = Convert.ToString(interp.m_apiHeader.formatType, 16);
            m_apiHeaderDataGridView.Rows[0].Cells[1].Value = "ASCII - " + System.Text.Encoding.ASCII.GetString(interp.m_apiHeader.partNo) + " Bytes[";
            for (int x = 0; x < interp.m_apiHeader.partNo.Length; x++)
            {
                m_apiHeaderDataGridView.Rows[0].Cells[1].Value += Convert.ToString(interp.m_apiHeader.partNo[x]);
            }
            m_apiHeaderDataGridView.Rows[0].Cells[1].Value += "]";

            m_apiHeaderDataGridView.Rows[0].Cells[2].Value = Convert.ToString(interp.m_apiHeader.blockNo, 16);
            
            m_apiHeaderDataGridView.Rows[0].Cells[3].Value = Convert.ToString(interp.m_apiHeader.noOfBlocks, 16);

            m_apiHeaderDataGridView.Rows[0].Cells[4].Value = "ASCII - " + System.Text.Encoding.ASCII.GetString(interp.m_apiHeader.dataCreationDate) + " Bytes[";
            for (int x = 0; x < interp.m_apiHeader.dataCreationDate.Length; x++)
            {
                m_apiHeaderDataGridView.Rows[0].Cells[4].Value += Convert.ToString(interp.m_apiHeader.dataCreationDate[x]);
            }
            m_apiHeaderDataGridView.Rows[0].Cells[4].Value += "]";

            m_apiHeaderDataGridView.Rows[0].Cells[5].Value = Convert.ToString(interp.m_apiHeader.dataType, 16);
            
            for (int x = 0; x < interp.m_apiHeader.spare.Length; x++)
            {
                m_apiHeaderDataGridView.Rows[0].Cells[6].Value += Convert.ToString(interp.m_apiHeader.spare[x]);
            }
            
            m_apiHeaderDataGridView.Rows[0].Cells[7].Value = Convert.ToString(interp.m_apiHeader.noOfAddressBytes, 16);
            m_apiHeaderDataGridView.Rows[0].Cells[8].Value = Convert.ToString(interp.m_apiHeader.noOfDataBytes, 16);
            m_apiHeaderDataGridView.Rows[0].Cells[9].Value = Convert.ToString(interp.m_apiHeader.crcType, 16);
            m_apiHeaderDataGridView.Rows[0].Cells[10].Value = Convert.ToString(interp.m_apiHeader.noOfCRCBytes, 16);

            m_headerDataGridView.Rows.Add();
            m_headerDataGridView.Rows[0].Cells[0].Value = Convert.ToString(interp.m_header.checksum,16);
            m_headerDataGridView.Rows[0].Cells[1].Value = Convert.ToString(interp.m_header.moduleID,16);
            m_headerDataGridView.Rows[0].Cells[2].Value = Convert.ToString(interp.m_header.partNo,16);
            m_headerDataGridView.Rows[0].Cells[3].Value = Convert.ToString(interp.m_header.designLevel,16);
            m_headerDataGridView.Rows[0].Cells[4].Value = Convert.ToString(interp.m_header.headerType,16);
            m_headerDataGridView.Rows[0].Cells[5].Value = interp.m_header.interpType.ToString();
            m_headerDataGridView.Rows[0].Cells[6].Value = Convert.ToString(interp.m_header.routineSectionOffset,16);
            m_headerDataGridView.Rows[0].Cells[7].Value = Convert.ToString(interp.m_header.addType, 16);
            m_headerDataGridView.Rows[0].Cells[8].Value = Convert.ToString(interp.m_header.dataAddressInfo,16);
            m_headerDataGridView.Rows[0].Cells[9].Value = Convert.ToString(interp.m_header.dataBytesPerMessage,16);

            foreach (Interpreter.InterpreterInstruction inst in interp.m_interpreterInstructions)
            {
                m_interpInstDataGridView.Rows.Add(
                    Convert.ToString(inst.step,16),
                    Convert.ToString(inst.opCode, 16) + " " + interp.m_opCodeHandler.OpCodeToString(inst.opCode),
                    Convert.ToString(inst.actionFields[0], 16) + " " +
                    Convert.ToString(inst.actionFields[1], 16) + " " +
                    Convert.ToString(inst.actionFields[2], 16) + " " +
                    Convert.ToString(inst.actionFields[3], 16),
                    Convert.ToString(inst.gotoFields[0], 16) + " " +
                    Convert.ToString(inst.gotoFields[1], 16) + " " +
                    Convert.ToString(inst.gotoFields[2], 16) + " " +
                    Convert.ToString(inst.gotoFields[3], 16) + " " +
                    Convert.ToString(inst.gotoFields[4], 16) + " " +
                    Convert.ToString(inst.gotoFields[5], 16) + " " +
                    Convert.ToString(inst.gotoFields[6], 16) + " " +
                    Convert.ToString(inst.gotoFields[7], 16) + " " +
                    Convert.ToString(inst.gotoFields[8], 16) + " " +
                    Convert.ToString(inst.gotoFields[9],16));
            }
            foreach (Interpreter.Routine rout in interp.m_routines)
            {
                string data="";
                foreach (byte b in rout.data.ToList())
                {
                    data += Convert.ToString(b,16);
                }
                m_routineDataGridView.Rows.Add(
                    Convert.ToString(rout.address,16),
                    Convert.ToString(rout.length,16),
                    data);

            }           
            foreach (List<byte> calFile in interp.m_calibrationModules)
            {
                string data="";
                foreach (byte b in calFile)
                {
                    data += Convert.ToString(b,16);
                }
                m_calFilesDataGridView.Rows.Add(data);
            }
        
        }
    }
}
