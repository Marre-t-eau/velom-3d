package org.velom3d.ble

import android.Manifest
import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothGatt
import android.bluetooth.BluetoothGattCallback
import android.bluetooth.BluetoothGattCharacteristic
import android.bluetooth.BluetoothGattDescriptor
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothProfile
import android.bluetooth.BluetoothStatusCodes
import android.bluetooth.le.ScanCallback
import android.bluetooth.le.ScanFilter
import android.bluetooth.le.ScanResult
import android.bluetooth.le.ScanSettings
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.ParcelUuid
import android.os.SystemClock
import org.godotengine.godot.Godot
import org.godotengine.godot.plugin.GodotPlugin
import org.godotengine.godot.plugin.SignalInfo
import org.godotengine.godot.plugin.UsedByGodot
import java.util.UUID

/**
 * Bluetooth Low Energy pour Velom3d, exposé à Godot sous le singleton « Velom3dBle ».
 *
 * Le plugin ne fait que transporter des octets : scan filtré sur les services utiles,
 * connexion, abonnement aux notifications, écriture. Tout le décodage est fait côté C#
 * (Sensors/Core), le même code que pour le simulateur. Côté jeu : NativeBleBackend.cs.
 *
 * Toutes les opérations Bluetooth passent par le thread principal ([main]) : Android
 * n'accepte qu'une opération GATT à la fois par appareil, d'où la file [Link.ops].
 */
@SuppressLint("MissingPermission") // vérifiées par hasPermissions() avant scan et connexion
class Velom3dBlePlugin(godot: Godot) : GodotPlugin(godot) {

    private val main = Handler(Looper.getMainLooper())
    private val links = HashMap<String, Link>()
    private val lastSeen = HashMap<String, Long>()
    private var scanning = false

    private val appContext: Context? get() = activity?.applicationContext
    private val adapter: BluetoothAdapter?
        get() = appContext?.getSystemService(BluetoothManager::class.java)?.adapter

    override fun getPluginName() = "Velom3dBle"

    override fun getPluginSignals(): Set<SignalInfo> = setOf(
        SignalInfo("scan_result", String::class.java, String::class.java, String::class.java, Int::class.javaObjectType),
        SignalInfo("scan_finished"),
        SignalInfo("connected", String::class.java, String::class.java),
        SignalInfo("disconnected", String::class.java),
        SignalInfo("notified", String::class.java, String::class.java, ByteArray::class.java),
        SignalInfo("read_result", String::class.java, String::class.java, ByteArray::class.java),
        SignalInfo("error", String::class.java),
        SignalInfo("permissions_result", Boolean::class.javaObjectType),
    )

    // --- Autorisations ---

    private fun requiredPermissions(): Array<String> =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S)
            arrayOf(Manifest.permission.BLUETOOTH_SCAN, Manifest.permission.BLUETOOTH_CONNECT)
        else
            arrayOf(Manifest.permission.ACCESS_FINE_LOCATION)

    @UsedByGodot
    fun hasPermissions(): Boolean {
        val ctx = appContext ?: return false
        return requiredPermissions().all { ctx.checkSelfPermission(it) == PackageManager.PERMISSION_GRANTED }
    }

    @UsedByGodot
    fun requestPermissions() {
        val act = activity ?: return
        if (hasPermissions()) {
            emit("permissions_result", true)
            return
        }
        act.runOnUiThread { act.requestPermissions(requiredPermissions(), PERMISSION_REQUEST) }
    }

    override fun onMainRequestPermissionsResult(requestCode: Int, permissions: Array<String>, grantResults: IntArray) {
        if (requestCode == PERMISSION_REQUEST) emit("permissions_result", hasPermissions())
    }

    // --- Scan ---

    @UsedByGodot
    fun startScan(timeoutMs: Int) {
        main.post {
            val bt = adapter
            if (bt == null || !hasPermissions() || !bt.isEnabled) {
                emit("error", when {
                    bt == null -> "Cet appareil n'a pas de Bluetooth."
                    !hasPermissions() -> "Autorisation Bluetooth manquante."
                    else -> "Le Bluetooth est désactivé. Activez-le puis relancez la recherche."
                })
                emit("scan_finished")
                return@post
            }
            stopScanNow(notify = false)
            val scanner = bt.bluetoothLeScanner ?: return@post emit("scan_finished")
            val filters = SCAN_SERVICES.map { ScanFilter.Builder().setServiceUuid(ParcelUuid(it)).build() }
            val settings = ScanSettings.Builder().setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY).build()
            lastSeen.clear()
            scanner.startScan(filters, settings, scanCallback)
            scanning = true
            main.postDelayed(scanTimeout, timeoutMs.toLong().coerceAtLeast(1000))
        }
    }

    @UsedByGodot
    fun stopScan() {
        main.post { stopScanNow(notify = true) }
    }

    private val scanTimeout = Runnable { stopScanNow(notify = true) }

    private fun stopScanNow(notify: Boolean) {
        main.removeCallbacks(scanTimeout)
        if (!scanning) return
        scanning = false
        val bt = adapter
        if (bt != null && bt.isEnabled) bt.bluetoothLeScanner?.stopScan(scanCallback)
        if (notify) emit("scan_finished")
    }

    private val scanCallback = object : ScanCallback() {
        override fun onScanResult(callbackType: Int, result: ScanResult) {
            val address = result.device.address
            // Un appareil s'annonce plusieurs fois par seconde : une remontée par seconde suffit.
            val now = SystemClock.elapsedRealtime()
            if (now - (lastSeen[address] ?: Long.MIN_VALUE / 2) < 1000) return
            lastSeen[address] = now
            val record = result.scanRecord
            val name = record?.deviceName ?: result.device.name ?: ""
            val services = record?.serviceUuids.orEmpty().joinToString(",") { it.uuid.toString() }
            emit("scan_result", address, name, services, result.rssi)
        }

        override fun onScanFailed(errorCode: Int) {
            scanning = false
            main.removeCallbacks(scanTimeout)
            emit("error", "La recherche Bluetooth a échoué (code $errorCode).")
            emit("scan_finished")
        }
    }

    // --- Connexion ---

    @UsedByGodot
    fun connectDevice(address: String) {
        main.post {
            if (links.containsKey(address)) return@post
            val ctx = appContext
            val device = try {
                adapter?.getRemoteDevice(address)
            } catch (e: IllegalArgumentException) {
                null
            }
            if (ctx == null || device == null || !hasPermissions()) {
                emit("error", "Connexion impossible à $address.")
                emit("disconnected", address)
                return@post
            }
            val link = Link(address)
            links[address] = link
            link.gatt = device.connectGatt(ctx, false, link, BluetoothDevice.TRANSPORT_LE)
        }
    }

    @UsedByGodot
    fun disconnectDevice(address: String) {
        main.post {
            val link = links.remove(address) ?: return@post
            link.close()
            emit("disconnected", address)
        }
    }

    @UsedByGodot
    fun write(address: String, charUuid: String, data: ByteArray) {
        main.post {
            val link = links[address] ?: return@post
            link.enqueue(GattOp.Write(UUID.fromString(charUuid), data.copyOf()))
        }
    }

    override fun onMainDestroy() {
        stopScanNow(notify = false)
        links.values.forEach { it.close() }
        links.clear()
        super.onMainDestroy()
    }

    /** emitSignal repasse lui-même sur le thread de rendu de Godot. */
    private fun emit(signal: String, vararg args: Any) = emitSignal(signal, *args)

    private sealed class GattOp {
        class Subscribe(val characteristic: BluetoothGattCharacteristic) : GattOp()
        class Write(val uuid: UUID, val data: ByteArray) : GattOp()
        class Read(val characteristic: BluetoothGattCharacteristic) : GattOp()
        /** Fin des abonnements : l'appareil est prêt, on prévient le jeu. */
        object Ready : GattOp()
    }

    /** Une connexion GATT. Les callbacks arrivent sur un thread Binder : on repasse sur [main]. */
    private inner class Link(val address: String) : BluetoothGattCallback() {
        var gatt: BluetoothGatt? = null
        private val ops = ArrayDeque<GattOp>()
        private var busy = false
        private var services = ""
        private val opTimeout = Runnable { done() }

        fun close() {
            main.removeCallbacks(opTimeout)
            ops.clear()
            gatt?.disconnect()
            gatt?.close()
            gatt = null
        }

        fun enqueue(op: GattOp) {
            ops.addLast(op)
            if (!busy) next()
        }

        private fun done() {
            main.removeCallbacks(opTimeout)
            busy = false
            next()
        }

        private fun next() {
            val g = gatt ?: return
            val op = ops.removeFirstOrNull() ?: return
            val started = when (op) {
                is GattOp.Subscribe -> subscribe(g, op.characteristic)
                is GattOp.Write -> write(g, op)
                is GattOp.Read -> g.readCharacteristic(op.characteristic)
                GattOp.Ready -> {
                    emit("connected", address, services)
                    false
                }
            }
            if (!started) return next()
            busy = true
            // Filet de sécurité : certains appareils ne confirment pas toujours.
            main.postDelayed(opTimeout, OP_TIMEOUT_MS)
        }

        private fun subscribe(g: BluetoothGatt, ch: BluetoothGattCharacteristic): Boolean {
            val cccd = ch.getDescriptor(CCCD) ?: return false
            if (!g.setCharacteristicNotification(ch, true)) return false
            val indicateOnly = ch.properties and BluetoothGattCharacteristic.PROPERTY_NOTIFY == 0
            val value = if (indicateOnly) BluetoothGattDescriptor.ENABLE_INDICATION_VALUE
            else BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE
            return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                g.writeDescriptor(cccd, value) == BluetoothStatusCodes.SUCCESS
            } else {
                @Suppress("DEPRECATION")
                cccd.value = value
                @Suppress("DEPRECATION")
                g.writeDescriptor(cccd)
            }
        }

        private fun write(g: BluetoothGatt, op: GattOp.Write): Boolean {
            val ch = g.services.flatMap { it.characteristics }.firstOrNull { it.uuid == op.uuid }
            if (ch == null) {
                emit("error", "Caractéristique ${op.uuid} absente sur $address.")
                return false
            }
            val type = if (ch.properties and BluetoothGattCharacteristic.PROPERTY_WRITE != 0)
                BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT
            else BluetoothGattCharacteristic.WRITE_TYPE_NO_RESPONSE
            return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                g.writeCharacteristic(ch, op.data, type) == BluetoothStatusCodes.SUCCESS
            } else {
                ch.writeType = type
                @Suppress("DEPRECATION")
                ch.value = op.data
                @Suppress("DEPRECATION")
                g.writeCharacteristic(ch)
            }
        }

        override fun onConnectionStateChange(g: BluetoothGatt, status: Int, newState: Int) {
            main.post {
                if (links[address] !== this) return@post // déjà déconnecté par le jeu
                if (status == BluetoothGatt.GATT_SUCCESS && newState == BluetoothProfile.STATE_CONNECTED) {
                    g.discoverServices()
                    return@post
                }
                if (newState == BluetoothProfile.STATE_CONNECTED) return@post
                links.remove(address)
                close()
                if (status != BluetoothGatt.GATT_SUCCESS)
                    emit("error", "Connexion perdue avec $address (code $status).")
                emit("disconnected", address)
            }
        }

        override fun onServicesDiscovered(g: BluetoothGatt, status: Int) {
            main.post {
                if (links[address] !== this) return@post
                if (status != BluetoothGatt.GATT_SUCCESS) {
                    links.remove(address)
                    close()
                    emit("error", "Services Bluetooth illisibles sur $address (code $status).")
                    emit("disconnected", address)
                    return@post
                }
                services = g.services.joinToString(",") { it.uuid.toString() }
                g.services.flatMap { it.characteristics }
                    .filter { it.uuid in SUBSCRIBE }
                    .forEach { enqueue(GattOp.Subscribe(it)) }
                // Fiche de l'appareil (fabricant, plages de puissance…), lue une fois avant d'annoncer la connexion.
                g.services.flatMap { it.characteristics }
                    .filter { it.uuid in READ && it.properties and BluetoothGattCharacteristic.PROPERTY_READ != 0 }
                    .forEach { enqueue(GattOp.Read(it)) }
                enqueue(GattOp.Ready)
            }
        }

        override fun onDescriptorWrite(g: BluetoothGatt, d: BluetoothGattDescriptor, status: Int) {
            main.post { if (busy) done() }
        }

        override fun onCharacteristicWrite(g: BluetoothGatt, ch: BluetoothGattCharacteristic, status: Int) {
            main.post {
                if (status != BluetoothGatt.GATT_SUCCESS) emit("error", "Écriture refusée par $address (code $status).")
                if (busy) done()
            }
        }

        override fun onCharacteristicRead(g: BluetoothGatt, ch: BluetoothGattCharacteristic, value: ByteArray, status: Int) {
            main.post {
                if (status == BluetoothGatt.GATT_SUCCESS) emit("read_result", address, ch.uuid.toString(), value.copyOf())
                if (busy) done()
            }
        }

        @Deprecated("Remplacée par la version avec value à partir d'Android 13")
        override fun onCharacteristicRead(g: BluetoothGatt, ch: BluetoothGattCharacteristic, status: Int) {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) return
            @Suppress("DEPRECATION")
            val value = ch.value ?: ByteArray(0)
            main.post {
                if (status == BluetoothGatt.GATT_SUCCESS) emit("read_result", address, ch.uuid.toString(), value.copyOf())
                if (busy) done()
            }
        }

        override fun onCharacteristicChanged(g: BluetoothGatt, ch: BluetoothGattCharacteristic, value: ByteArray) {
            emit("notified", address, ch.uuid.toString(), value.copyOf())
        }

        @Deprecated("Remplacée par la version avec value à partir d'Android 13")
        override fun onCharacteristicChanged(g: BluetoothGatt, ch: BluetoothGattCharacteristic) {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) return
            @Suppress("DEPRECATION")
            val value = ch.value ?: return
            emit("notified", address, ch.uuid.toString(), value.copyOf())
        }
    }

    private companion object {
        const val PERMISSION_REQUEST = 0x7E1D
        const val OP_TIMEOUT_MS = 3000L

        fun sig(short: String): UUID = UUID.fromString("0000$short-0000-1000-8000-00805f9b34fb")

        val CCCD: UUID = sig("2902")

        /** Services recherchés : home trainer FTMS, puissance, cardio, vitesse/cadence. */
        val SCAN_SERVICES = listOf(sig("1826"), sig("1818"), sig("180d"), sig("1816"))

        /** Mesures (notify) et Control Point FTMS (indicate) auxquels on s'abonne. */
        /** Lues une fois à la connexion : informations de l'appareil, plages FTMS, fonctions. */
        val READ = setOf(
            sig("2a29"), sig("2a24"), sig("2a25"), sig("2a27"), sig("2a26"), sig("2a28"),
            sig("2acc"), sig("2ad5"), sig("2ad6"), sig("2ad8"), sig("2a65"), sig("2a5d"), sig("2a38"),
        )

        val SUBSCRIBE = setOf(sig("2ad2"), sig("2ad9"), sig("2ada"), sig("2a63"), sig("2a37"), sig("2a5b"))
    }
}
