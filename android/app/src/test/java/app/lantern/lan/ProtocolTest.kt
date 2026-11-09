package app.lantern.lan

import org.junit.Assert.*
import org.junit.Test
import java.security.Signature
import javax.crypto.Cipher
import javax.crypto.spec.IvParameterSpec
import javax.crypto.spec.SecretKeySpec
import java.security.KeyFactory
import java.security.KeyPair
import java.security.spec.PKCS8EncodedKeySpec
import java.security.spec.X509EncodedKeySpec
import java.util.Properties
import java.util.Base64
import java.io.File

class ProtocolTest {
    @Test fun decryptsRealWindows591PacketAndProducesReplyFixture() {
        // Captured from the authenticode-verified official 5.9.1 executable on loopback.
        // The fixture private key is a throwaway test identity, never an application key.
        val f = Properties().apply { ProtocolTest::class.java.getResourceAsStream("/windows-5.9.1.properties")!!.use { load(it) } }
        val factory = KeyFactory.getInstance("RSA")
        val crypto = IpCrypto(KeyPair(factory.generatePublic(X509EncodedKeySpec(Base64.getDecoder().decode(f.getProperty("publicKey")))), factory.generatePrivate(PKCS8EncodedKeySpec(Base64.getDecoder().decode(f.getProperty("privateKey"))))))
        val peer = IpCrypto.parseKey(f.getProperty("windowsKey"))
        assertEquals("b9aa5bbdc1cb8f4f", IpCrypto.userDigest(peer.key))
        val result = crypto.decrypt(f.getProperty("body"), f.getProperty("number").toLong(), peer)
        assertEquals("Windows 5.9.1 interoperability probe\u0000", result.bytes.toString(Charsets.UTF_8))
        assertTrue(result.signed)
        val n = System.currentTimeMillis() / 1000
        val reply = crypto.encrypt("Kotlin interoperability — مرحبا\u0000".toByteArray(), n, peer)
        File("build/interop-output.properties").writer().use { out ->
            Properties().apply { setProperty("number", n.toString()); setProperty("body", reply); setProperty("publicBody", crypto.publicBody()) }.store(out, "Ephemeral test peer; classic IPMsg Kotlin reply")
        }
    }
    @Test fun regularFileHeaderCountsUtf8Bytes() {
        val header = Wire.fileHeader(Wire.Attachment(0, "مرحبا.txt", 1234))
        assertEquals(header.size, header.copyOfRange(0, 4).toString(Charsets.US_ASCII).toInt(16))
        assertTrue(header.toString(Charsets.UTF_8).endsWith(":4d2:1:"))
    }
    @Test fun unicodeColonsAndAttachmentSurvivePacket() {
        val file = Wire.Attachment(7, "photo: summer.jpg", 0x100000001)
        val p = Wire.Packet(123, "android", "phone", 0x20L or Wire.UTF8 or Wire.FILE, "Hello: مرحبا 🌍\u0000${file.offer()}")
        assertEquals(p, Wire.parse(p.bytes()))
        assertEquals(file, Wire.attachments(p.body.substringAfter('\u0000')).single())
    }
    @Test fun handlesEmptyAndEscapedNamesAndMultipleFiles() {
        val files = listOf(Wire.Attachment(0, "empty.txt", 0), Wire.Attachment(1, "a::b:", 456))
        assertEquals(files, Wire.attachments(files.joinToString("") { it.offer() }))
    }
    @Test fun ignoresDirectoriesAndMalformedMetadata() {
        assertEquals(emptyList<Wire.Attachment>(), Wire.attachments("0:dir:0:0:2:\u0007broken\u0007"))
        assertEquals(emptyList<Wire.Attachment>(), Wire.attachments("0:bad:-1:0:1:\u0007"))
    }
    @Test fun boundsAndIdentityValidation() {
        assertThrows(IllegalArgumentException::class.java) { Wire.parse("invalid".toByteArray()) }
        assertThrows(IllegalArgumentException::class.java) { Wire.Packet(1, "bad:user", "host", 1, "").bytes() }
        assertThrows(IllegalArgumentException::class.java) { Wire.Packet(1, "user", "host", 1, "a".repeat(40000)).bytes() }
        assertThrows(IllegalArgumentException::class.java) { Wire.parse("1:-1:u:h:32:bad".toByteArray()) }
    }
    @Test fun legacyCp932Decode() {
        val p = Wire.Packet(3, "test", "host", 32, "こんにちは")
        assertEquals(p, Wire.parse(p.bytes()))
    }
    @Test fun pathTraversalDoesNotEscapeDestinationName() {
        assertEquals("evil.txt", Wire.safeName("../../evil.txt"))
        assertEquals("evil.txt", Wire.safeName("C:\\path\\evil.txt"))
        assertEquals("received-file", Wire.safeName(".."))
    }
    @Test fun duplicateWindowIsBounded() {
        val r = ReplayWindow(2)
        assertTrue(r.first("a")); assertFalse(r.first("a")); assertTrue(r.first("b")); assertTrue(r.first("c")); assertTrue(r.first("a"))
    }
    @Test fun packetIvIsAsciiZeroPadded() {
        assertArrayEquals(byteArrayOf(49, 50, 51) + ByteArray(13), IpCrypto.iv(123, true))
        assertArrayEquals(ByteArray(16), IpCrypto.iv(123, false))
    }
    @Test fun publicKeyAndEncryptionRoundTripHexAndBase64() {
        val sender = IpCrypto.generate(); val receiver = IpCrypto.generate()
        for (flags in listOf(IpCrypto.CAPS, IpCrypto.RSA or IpCrypto.AES, IpCrypto.CAPS and IpCrypto.B64.inv() and IpCrypto.SHA256.inv())) {
            val rk = IpCrypto.parseKey(receiver.publicBody()).copy(capabilities = flags)
            val plain = "Hello 🌍\u0000${Wire.Attachment(0, "data.txt", 7).offer()}\u0000".toByteArray()
            val body = sender.encrypt(plain, 54321, rk)
            val result = receiver.decrypt(body, 54321, IpCrypto.parseKey(sender.publicBody()))
            assertArrayEquals(plain, result.bytes)
            assertEquals(flags and (IpCrypto.SHA1 or IpCrypto.SHA256) != 0L, result.signed)
        }
    }
    @Test fun independentlyDecodesWireCiphertextAndSignature() {
        val sender = IpCrypto.generate(); val receiver = IpCrypto.generate()
        val plain = "wire compatibility\u0000".toByteArray()
        val body = sender.encrypt(plain, 100, IpCrypto.parseKey(receiver.publicBody()))
        val a = body.split(':')
        val key = Cipher.getInstance("RSA/ECB/PKCS1Padding").apply { init(Cipher.DECRYPT_MODE, receiver.pair.private) }.doFinal(java.util.Base64.getDecoder().decode(a[1]))
        val decoded = Cipher.getInstance("AES/CBC/PKCS5Padding").apply {
            init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), IvParameterSpec("100".toByteArray() + ByteArray(13)))
        }.doFinal(java.util.Base64.getDecoder().decode(a[2]))
        assertArrayEquals(plain, decoded)
        val verifier = Signature.getInstance("SHA256withRSA").apply { initVerify(sender.pair.public); update(decoded) }
        assertTrue(verifier.verify(java.util.Base64.getDecoder().decode(a[3])))
    }
    @Test fun badSignatureAndUnknownSignedPeerAreRejected() {
        val sender = IpCrypto.generate(); val receiver = IpCrypto.generate(); val impostor = IpCrypto.generate()
        val body = sender.encrypt("hello\u0000".toByteArray(), 100, IpCrypto.parseKey(receiver.publicBody()))
        assertThrows(IllegalArgumentException::class.java) { receiver.decrypt(body, 100, IpCrypto.parseKey(impostor.publicBody())) }
        assertThrows(IllegalArgumentException::class.java) { receiver.decrypt(body, 100, null) }
    }
}
