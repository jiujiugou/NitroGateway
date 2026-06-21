import { defineStore } from 'pinia'
import { ref } from 'vue'
import type { Device } from '../api/types'
import { getDevices } from '../api/devices'

export const useDeviceStore = defineStore('device', () => {
  const devices = ref<Device[]>([])
  const loading = ref(false)

  async function fetch() { loading.value = true; try { devices.value = await getDevices() } finally { loading.value = false } }
  return { devices, loading, fetch }
})
