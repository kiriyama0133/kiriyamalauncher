#include "framework/winsock_api.h"

SocketFn original_socket = nullptr;
WSASocketWFn original_WSASocketW = nullptr;
BindFn original_bind = nullptr;
ConnectFn original_connect = nullptr;
GetSockNameFn original_getsockname = nullptr;
CloseSocketFn original_closesocket = nullptr;
SendToFn original_sendto = nullptr;
WSASendToFn original_WSASendTo = nullptr;
SendFn original_send = nullptr;
WSASendFn original_WSASend = nullptr;
RecvFromFn original_recvfrom = nullptr;
WSARecvFromFn original_WSARecvFrom = nullptr;
RecvFn original_recv = nullptr;
WSARecvFn original_WSARecv = nullptr;
SelectFn original_select = nullptr;
WSAPollFn original_WSAPoll = nullptr;
GetProcAddressFn original_GetProcAddress = nullptr;
