﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Serilog;
using server;
using System.Net;
using System.Net.Sockets;
using server.Types.ClientReport;
using server.Types.ClientSend;
using System.Runtime.InteropServices;
using server.Database.Model;
using server.Database.Entity;
using Org.BouncyCastle.Asn1.BC;

namespace server
{
    public class Message
    {
        private byte[] _buffer;
        private int _bufferSize;
        private ILogger _logger;
        private PACKET_HEADER _header;
        private NetworkStream _networkStream;
        private TcpClient _tcpClient;

        private enum MESSAGE_TYPE
        {
            MESSAGE_TYPE_CLIENT_REPORT = 1,
            MESSAGE_TYPE_CLIENT_SEND = 2,
            MESSAGE_TYPE_CLIENT_REQUEST = 3
        }

        private enum CLIENT_SEND_REQUEST_ID
        {
            SYSTEM_INFORMATION = 10
        }

        public struct PACKET_HEADER
        {
            public int message_type;
            public ulong steam64_id;
        };

        private struct PACKET_REQUEST_HEADER
        {
            public int RequestId;
        }

        private struct SYSTEM_INFORMATION_REQUEST_RESPONSE
        {
            public int RequestId;
            public int CanUserProceed;
            public int reason;
        }

        private struct REPORT_RESPONSE
        {
            public int success;
        }

        private enum USER_BAN_REASONS
        {
            HARDWARE_BAN = 10,
            USER_BAN = 20
        }

        private enum ReportCodes
        {
            REPORT_CODE_MODULE_VERIFICATION = 10,
            REPORT_CODE_START_ADDRESS_VERIFICATION = 20,
            REPORT_PAGE_PROTECTION_VERIFICATION = 30,
            REPORT_PATTERN_SCAN_FAILURE = 40,
            REPORT_NMI_CALLBACK_FAILURE = 50,
            REPORT_MODULE_VALIDATION_FAILURE = 60,
            REPORT_ILLEGAL_HANDLE_OPERATION = 70,
            REPORT_INVALID_PROCESS_ALLOCATION = 80,
            REPORT_HIDDEN_SYSTEM_THREAD = 90,
            REPORT_ILLEGAL_ATTACH_PROCESS = 100
        }

        public Message(TcpClient client, byte[] buffer, int bufferSize, ILogger logger)
        {
            _tcpClient = client;
            _networkStream = client.GetStream();
            _buffer = buffer;
            _bufferSize = bufferSize;
            _logger = logger;
            _header = this.GetMessageHeader();

            switch (_header.message_type)
            {
                case (int)MESSAGE_TYPE.MESSAGE_TYPE_CLIENT_REPORT:
                    int reportId = GetPacketRequestId().RequestId;
                    this.HandleReportMessage(reportId);
                    break;
                case (int)MESSAGE_TYPE.MESSAGE_TYPE_CLIENT_SEND:
                    int requestId = GetPacketRequestId().RequestId;
                    this.HandleClientSendMessage(requestId);
                    break;
                default:
                    _logger.Information("This message type is not accepted at the moment.");
                    break;
            }
        }
        
        private PACKET_HEADER GetMessageHeader()
        {
            return Helper.BytesToStructure<PACKET_HEADER>(_buffer, 0);
        }

        unsafe private PACKET_REQUEST_HEADER GetPacketRequestId()
        {
            return Helper.BytesToStructure<PACKET_REQUEST_HEADER>(_buffer, sizeof(PACKET_HEADER));
        }

        unsafe private CLIENT_SEND_PACKET_HEADER GetClientSendPacketHeader()
        {
            return Helper.BytesToStructure<CLIENT_SEND_PACKET_HEADER>(_buffer, sizeof(PACKET_HEADER));
        }

        unsafe private void HandleReportMessage(int reportId)
        { 
            switch (reportId)
            {
                case (int)ReportCodes.REPORT_ILLEGAL_HANDLE_OPERATION:
                    _logger.Information("REPORT_ILLEGAL_HANDLE_OPERATION");
                    break;
                case (int)ReportCodes.REPORT_CODE_MODULE_VERIFICATION:
                    _logger.Information("REPORT_CODE_MODULE_VERIFICATION");
                    break;
                case (int)ReportCodes.REPORT_NMI_CALLBACK_FAILURE:
                    _logger.Information("REPORT_NMI_CALLBACK_FAILURE");
                    break;
                case (int)ReportCodes.REPORT_MODULE_VALIDATION_FAILURE:
                    _logger.Information("REPORT_MODULE_VALIDATION_FAILURE");
                    break;
                default:
                    _logger.Information("Report code not handled yet");
                    break;
            }

/*            OPEN_HANDLE_FAILURE_REPORT openHandleFailure = 
                Helper.BytesToStructure<OPEN_HANDLE_FAILURE_REPORT>(_buffer, sizeof(PACKET_HEADER));*/

/*            _logger.Information("Report code: {0}, ProcessID: {1:x}, ThreadId: {2:x}, DesiredAccess{3:x}",
                openHandleFailure.ReportCode,
                openHandleFailure.ProcessId,
                openHandleFailure.ThreadId,
                openHandleFailure.DesiredAccess);*/

            BuildReportResponseMessage(1);
        }

        private void BuildReportResponseMessage(int success)
        {
            REPORT_RESPONSE response = new REPORT_RESPONSE();
            response.success = 1;

            byte[] responseBytes = Helper.StructureToBytes<REPORT_RESPONSE>(ref response);

            _networkStream.Write(responseBytes, 0, responseBytes.Length);
        }

        private void HandleClientSendMessage(int clientSendId)
        {
            CLIENT_SEND_PACKET_HEADER header = GetClientSendPacketHeader();

            switch (header.RequestId)
            {
                case (int)CLIENT_SEND_REQUEST_ID.SYSTEM_INFORMATION:
                    this.HandleClientSendHardwareInformation(header);
                    break;
            }

        }

        unsafe private void HandleClientSendHardwareInformation(CLIENT_SEND_PACKET_HEADER sendPacketHeader)
        {
            _logger.Information("Handling client send hardware information");

            PACKET_CLIENT_HARDWARE_INFORMATION info = Helper.BytesToStructure<PACKET_CLIENT_HARDWARE_INFORMATION>(_buffer, sizeof(PACKET_HEADER) + sizeof(CLIENT_SEND_PACKET_HEADER));

            _logger.Information("SteamId: {0}, Mobo Serial: {1}, drive serial: {2}", _header.steam64_id, info.MotherboardSerialNumber, info.DeviceDriver0Serial);

            using (var context = new ModelContext())
            {
                context.Database.EnsureCreated();

                var user = new UserEntity(context)
                {
                    Steam64Id = _header.steam64_id
                };

                if (!user.CheckIfUserExists())
                {
                    _logger.Information("Creating new user");
                    user.InsertUser();
                }
                else if (user.CheckIfUserIsBanned())
                {
                    _logger.Information("User is banned");
                    BuildSystemVerificationResponseHeader(0, sendPacketHeader.RequestId, (int)USER_BAN_REASONS.USER_BAN);
                    return;
                }

                var hardwareConfiguration = new HardwareConfigurationEntity(context)
                {
                    DeviceDrive0Serial = info.DeviceDriver0Serial,
                    MotherboardSerial = info.MotherboardSerialNumber,
                    User = user 
                };

                if (hardwareConfiguration.CheckIfHardwareIsBanned())
                {
                    _logger.Information("Users hardware is banned");
                    BuildSystemVerificationResponseHeader(0, sendPacketHeader.RequestId, (int)USER_BAN_REASONS.HARDWARE_BAN);
                    return;
                }

                if (hardwareConfiguration.CheckIfHardwareExists())
                {
                    _logger.Information("User hardware already exists");
                    BuildSystemVerificationResponseHeader(1, sendPacketHeader.RequestId, 0);
                    return;
                }

                hardwareConfiguration.InsertHardwareConfiguration();
                BuildSystemVerificationResponseHeader(1, sendPacketHeader.RequestId, 0);
                context.SaveChanges();
            }
        }

        private void BuildSystemVerificationResponseHeader(int canUserProceed, int requestId, int reason)
        {
            SYSTEM_INFORMATION_REQUEST_RESPONSE response = new SYSTEM_INFORMATION_REQUEST_RESPONSE();
            response.CanUserProceed = canUserProceed;
            response.RequestId = requestId;
            response.reason = reason;

            byte[] responseBytes = Helper.StructureToBytes<SYSTEM_INFORMATION_REQUEST_RESPONSE>(ref response);

            _networkStream.Write(responseBytes, 0, responseBytes.Length);
        }
    }
}
 