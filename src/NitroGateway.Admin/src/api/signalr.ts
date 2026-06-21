import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'

export function createLiveConnection() {
  return new HubConnectionBuilder()
    .withUrl('http://localhost:5000/hubs/live')
    .configureLogging(LogLevel.Warning)
    .withAutomaticReconnect()
    .build()
}
