package app.lantern.lan

import java.math.BigInteger
import java.security.*
import java.security.interfaces.RSAPublicKey
import java.security.spec.RSAPublicKeySpec
import javax.crypto.Cipher
import javax.crypto.spec.IvParameterSpec
import javax.crypto.spec.SecretKeySpec
import java.util.Base64

/** IPMsg classic RSA-2048 / AES-256-CBC wire format, not a custom cryptosystem. */
class IpCrypto(val pair: KeyPair) {
    companion object {
        const val RSA = 4L
        const val AES = 0x00100000L
        const val IV = 0x00800000L
        const val B64 = 0x01000000L
        const val SHA1 = 0x20000000L
        const val SHA256 = 0x40000000L
        const val CAPS = RSA or AES or IV or B64 or SHA1 or SHA256
        fun generate() = IpCrypto(KeyPairGenerator.getInstance("RSA").apply { initialize(2048) }.generateKeyPair())
        data class PeerKey(val capabilities: Long, val key: PublicKey)
        fun parseKey(body: String): PeerKey {
            val a = body.substringBefore('\u0000').split(':', limit = 2)
            val b = a[1].split('-', limit = 2)
            val n = BigInteger(b[1], 16); val e = BigInteger(b[0], 16)
            require(n.bitLength() == 2048 && e >= BigInteger.valueOf(3) && e.bitLength() <= 32)
            return PeerKey(a[0].toLong(16), KeyFactory.getInstance("RSA").generatePublic(RSAPublicKeySpec(n, e)))
        }
        fun iv(number: Long, enabled: Boolean): ByteArray = ByteArray(16).also {
            if (enabled) number.toString().toByteArray().copyInto(it, endIndex = minOf(15, number.toString().length))
        }
        fun hex(bytes: ByteArray) = bytes.joinToString("") { "%02x".format(it.toInt() and 255) }
        fun unhex(s: String): ByteArray { require(s.length % 2 == 0); return s.chunked(2).map { it.toInt(16).toByte() }.toByteArray() }
        fun decode(s: String, flags: Long) = if (flags and B64 != 0L) Base64.getDecoder().decode(s) else unhex(s)
        fun encode(b: ByteArray, flags: Long) = if (flags and B64 != 0L) Base64.getEncoder().encodeToString(b) else hex(b)
        fun compatible(key: PeerKey?) = key != null && key.capabilities and (RSA or AES) == (RSA or AES)
        fun userDigest(key: PublicKey): String {
            val modulus = (key as RSAPublicKey).modulus.toByteArray().takeLast(256).toByteArray().reversedArray()
            val hash = MessageDigest.getInstance("SHA-1").digest(modulus).reversedArray().copyOf(24)
            return hex(ByteArray(8) { (hash[it].toInt() xor hash[it + 8].toInt() xor hash[it + 16].toInt()).toByte() })
        }
    }
    fun publicBody(): String {
        val k = pair.public as RSAPublicKey
        return "${CAPS.toString(16)}:${k.publicExponent.toString(16)}-${k.modulus.toString(16)}"
    }
    fun encrypt(plain: ByteArray, number: Long, peer: PeerKey): String {
        require(compatible(peer))
        var flags = peer.capabilities and CAPS
        flags = if (flags and SHA256 != 0L) flags and SHA1.inv() else flags
        val secret = ByteArray(32).also { SecureRandom().nextBytes(it) }
        val rsa = Cipher.getInstance("RSA/ECB/PKCS1Padding").apply { init(Cipher.ENCRYPT_MODE, peer.key) }.doFinal(secret)
        val aes = Cipher.getInstance("AES/CBC/PKCS5Padding").apply {
            init(Cipher.ENCRYPT_MODE, SecretKeySpec(secret, "AES"), IvParameterSpec(iv(number, flags and IV != 0L)))
        }.doFinal(plain)
        var result = "${flags.toString(16)}:${encode(rsa, flags)}:${encode(aes, flags)}"
        if (flags and (SHA1 or SHA256) != 0L) {
            val sign = Signature.getInstance(if (flags and SHA256 != 0L) "SHA256withRSA" else "SHA1withRSA")
            sign.initSign(pair.private); sign.update(plain)
            result += ":${encode(sign.sign(), flags)}"
        }
        return result
    }
    data class Decrypted(val bytes: ByteArray, val signed: Boolean)
    fun decrypt(body: String, number: Long, peer: PeerKey?): Decrypted {
        val a = body.substringBefore('\u0000').split(':')
        val flags = a[0].toLong(16)
        require(flags and (RSA or AES) == (RSA or AES)) { "Unsupported encryption" }
        val secret = Cipher.getInstance("RSA/ECB/PKCS1Padding").apply { init(Cipher.DECRYPT_MODE, pair.private) }.doFinal(decode(a[1], flags))
        require(secret.size == 32)
        val plain = Cipher.getInstance("AES/CBC/PKCS5Padding").apply {
            init(Cipher.DECRYPT_MODE, SecretKeySpec(secret, "AES"), IvParameterSpec(iv(number, flags and IV != 0L)))
        }.doFinal(decode(a[2], flags))
        val signed = flags and (SHA1 or SHA256) != 0L
        if (signed) {
            requireNotNull(peer) { "Sender key not available; refresh devices and retry" }
            val verify = Signature.getInstance(if (flags and SHA256 != 0L) "SHA256withRSA" else "SHA1withRSA")
            verify.initVerify(peer.key); verify.update(plain)
            require(verify.verify(decode(a[3], flags))) { "Invalid message signature" }
        }
        return Decrypted(plain, signed)
    }
}
