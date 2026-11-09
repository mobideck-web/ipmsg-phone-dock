package app.lantern.lan

import org.junit.Assert.*
import org.junit.Test
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketException

class LanRoutingTest {
    @Test fun unreachableBroadcastAndStaleAddressDoNotAbortDiscovery() {
        val attempted = mutableListOf<String>()
        val targets = listOf("255.255.255.255", "192.168.43.255", "10.0.0.99", "192.168.43.2")
        val sent = discoverIndependently(targets) {
            attempted += it
            if (it == targets[0] || it == targets[2]) throw SocketException("ENETUNREACH")
        }
        assertEquals(targets, attempted)
        assertEquals(2, sent)
    }
    @Test fun selectsHotspotByLongestPrefixWithoutInternetDefault() {
        val broad = LanRoute("10.1.0.1", 8, "10.255.255.255")
        val hotspot = LanRoute("10.83.148.1", 24, "10.83.148.255")
        assertEquals(hotspot, LanRoute.select(listOf(broad, hotspot), "10.83.148.65"))
        assertNull(LanRoute.select(listOf(hotspot), "192.168.1.10"))
        assertNull(LanRoute.select(emptyList(), "10.83.148.65"))
    }
    @Test fun checksSubnetBoundariesAndRejectsMalformedAddresses() {
        val lan = LanRoute("192.168.42.129", 25, "192.168.42.255")
        assertTrue(lan.contains("192.168.42.254"))
        assertFalse(lan.contains("192.168.42.127"))
        assertFalse(lan.contains("192.168.42.256"))
        assertFalse(lan.contains("some-host"))
        assertFalse(LanRoute("192.168.42.1", 0, null).contains("192.168.42.2"))
    }
    @Test fun sourceBoundUdpKeepsListeningPortAndReceivesReply() {
        DatagramSocket(null).use { wildcard ->
            wildcard.reuseAddress = true
            wildcard.bind(InetSocketAddress(0))
            DatagramSocket(null).use { lan ->
                lan.reuseAddress = true
                lan.bind(InetSocketAddress("127.0.0.1", wildcard.localPort))
                lan.soTimeout = 1000
                DatagramSocket(InetSocketAddress("127.0.0.1", 0)).use { peer ->
                    peer.soTimeout = 1000
                    lan.send(DatagramPacket(byteArrayOf(42), 1, InetAddress.getByName("127.0.0.1"), peer.localPort))
                    val packet = DatagramPacket(ByteArray(16), 16); peer.receive(packet)
                    assertEquals(wildcard.localPort, packet.port)
                    peer.send(DatagramPacket(byteArrayOf(43), 1, packet.address, packet.port))
                    val reply = DatagramPacket(ByteArray(16), 16); lan.receive(reply)
                    assertEquals(43, reply.data[0].toInt())
                }
            }
        }
    }
}
