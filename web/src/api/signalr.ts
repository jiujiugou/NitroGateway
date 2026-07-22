import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'

export function createLiveConnection() {
  const token = localStorage.getItem('token')
  return new HubConnectionBuilder()
    .withUrl('/hubs/live', { accessTokenFactory: () => token ?? '' })
    .configureLogging(LogLevel.Warning)
    .withAutomaticReconnect()
    .build()
}
