package app.lantern.lan

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.Socket

/** A directly attached LAN, including a phone-hosted hotspot with no upstream network. */
data class LanRoute(val address: String, val prefix: Int, val broadcast: String?) {
    fun contains(ip: String): Boolean {
        val local = ipv4(address) ?: return false
        val remote = ipv4(ip) ?: return false
        if (prefix !in 1..32) return false
        val mask = (0xffffffffL shl (32 - prefix)) and 0xffffffffL
        return local and mask == remote and mask
    }
    companion object {
        private fun ipv4(ip: String): Long? {
            val parts = ip.split('.')
            if (parts.size != 4) return null
            return parts.fold(0L) { value, part ->
                val octet = part.toIntOrNull()?.takeIf { it in 0..255 } ?: return null
                (value shl 8) or octet.toLong()
            }
        }
        fun select(routes: List<LanRoute>, ip: String) = routes.filter { it.contains(ip) }.maxByOrNull { it.prefix }
    }
}

object LanRouting {
    fun identity(context: Context, source: String): String? {
        if (source == "127.0.0.1") return "loopback"
        val route = routes().find { it.address == source } ?: return null
        return "${route.address}/${route.prefix}:${network(context, source)?.networkHandle}"
    }
    fun routes(): List<LanRoute> = runCatching {
        NetworkInterface.getNetworkInterfaces().toList()
            .filter { it.isUp && !it.isLoopback && !it.isPointToPoint }
            .flatMap { iface -> iface.interfaceAddresses.mapNotNull { item ->
                val address = item.address as? Inet4Address ?: return@mapNotNull null
                // Cellular interfaces often have an IPv4 address but no broadcast LAN.
                if (item.broadcast == null) return@mapNotNull null
                LanRoute(address.hostAddress!!, item.networkPrefixLength.toInt(), item.broadcast?.hostAddress)
            } }.distinctBy { it.address }
    }.getOrDefault(emptyList())

    @Suppress("DEPRECATION") // Snapshot at socket creation; callbacks plus polling handle changes.
    fun network(context: Context, source: String): Network? {
        val manager = context.getSystemService(ConnectivityManager::class.java) ?: return null
        return manager.allNetworks.firstOrNull { candidate ->
            manager.getLinkProperties(candidate)?.linkAddresses?.any { it.address.hostAddress == source } == true
        }
    }

    fun bind(context: Context, socket: DatagramSocket, route: LanRoute, port: Int, network: Network? = network(context, route.address)) {
        // Never depend on INTERNET/VALIDATED or the cellular default. A hosted hotspot may
        // have no ConnectivityManager Network: its directly attached source still works.
        network?.bindSocket(socket)
        socket.bind(InetSocketAddress(route.address, port))
    }

    fun bind(context: Context, socket: Socket, peer: String) {
        if (peer == "127.0.0.1") return
        val route = LanRoute.select(routes(), peer) ?: throw java.net.NoRouteToHostException("This file sender is outside the current local network. Reconnect to the same Wi-Fi or hotspot and request a fresh offer.")
        network(context, route.address)?.bindSocket(socket)
        socket.bind(InetSocketAddress(route.address, 0))
    }
}

/** A failed route must never prevent discovery on other interfaces or saved addresses. */
internal fun <T> discoverIndependently(targets: Iterable<T>, send: (T) -> Unit): Int {
    var sent = 0
    targets.forEach { target -> try { send(target); sent++ } catch (_: java.io.IOException) { } }
    return sent
}
