package app.lantern.lan

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.math.BigInteger
import java.security.*
import java.security.cert.X509Certificate
import java.util.Date
import javax.net.ssl.*
import org.bouncycastle.asn1.x500.X500Name
import org.bouncycastle.asn1.x509.*
import org.bouncycastle.cert.jcajce.JcaX509v3CertificateBuilder
import org.bouncycastle.cert.jcajce.JcaX509CertificateConverter
import org.bouncycastle.operator.jcajce.JcaContentSignerBuilder

data class CameraIdentity(val context: SSLContext, val certificate: X509Certificate) {
    val fingerprint get() = MessageDigest.getInstance("SHA-256").digest(certificate.encoded).joinToString(":") { "%02X".format(it.toInt() and 255) }
}
object CameraTls {
    private const val ALIAS = "ipmsg-camera-tls-ec-v2"
    fun load(context: Context, ip: String): CameraIdentity {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        if (!store.containsAlias(ALIAS)) KeyPairGenerator.getInstance("EC","AndroidKeyStore").apply {
            initialize(KeyGenParameterSpec.Builder(ALIAS,KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY)
                .setAlgorithmParameterSpec(java.security.spec.ECGenParameterSpec("secp256r1"))
                .setDigests(KeyProperties.DIGEST_NONE,KeyProperties.DIGEST_SHA256,KeyProperties.DIGEST_SHA384,KeyProperties.DIGEST_SHA512).build())
        }.generateKeyPair()
        val key = store.getKey(ALIAS,null) as PrivateKey
        val publicKey = store.getCertificate(ALIAS).publicKey
        val prefs = context.getSharedPreferences("camera-tls",Context.MODE_PRIVATE)
        val saved = runCatching { java.security.cert.CertificateFactory.getInstance("X.509").generateCertificate(java.util.Base64.getDecoder().decode(prefs.getString("certificate","")).inputStream()) as X509Certificate }.getOrNull()
        val cert = saved?.takeIf { prefs.getString("ip",null)==ip && it.notAfter.time > System.currentTimeMillis()+86_400_000 && it.publicKey.encoded.contentEquals(publicKey.encoded) }
            ?: certificate(KeyPair(publicKey,key),ip).also { prefs.edit().putString("ip",ip).putString("certificate",java.util.Base64.getEncoder().encodeToString(it.encoded)).commit() }
        return identity(key,cert)
    }
    fun certificate(pair: KeyPair, ip: String): X509Certificate {
        val now = System.currentTimeMillis(); val name = X500Name("CN=IPMsg Camera")
        val builder = JcaX509v3CertificateBuilder(name,BigInteger(159,SecureRandom()),Date(now-86_400_000),Date(now+180L*86_400_000),name,pair.public)
        builder.addExtension(Extension.basicConstraints,true,BasicConstraints(false))
        builder.addExtension(Extension.keyUsage,true,KeyUsage(KeyUsage.digitalSignature))
        builder.addExtension(Extension.extendedKeyUsage,false,ExtendedKeyUsage(KeyPurposeId.id_kp_serverAuth))
        builder.addExtension(Extension.subjectAlternativeName,false,GeneralNames(GeneralName(GeneralName.iPAddress,ip)))
        return JcaX509CertificateConverter().getCertificate(builder.build(JcaContentSignerBuilder(if(pair.public.algorithm == "EC") "SHA256withECDSA" else "SHA256withRSA").build(pair.private)))
    }
    fun identity(key: PrivateKey, cert: X509Certificate): CameraIdentity {
        val manager = object : X509ExtendedKeyManager() {
            override fun chooseServerAlias(type: String?, issuers: Array<out Principal>?, socket: java.net.Socket?) = if(type?.contains(cert.publicKey.algorithm)==true) ALIAS else null
            override fun chooseEngineServerAlias(type: String?, issuers: Array<out Principal>?, engine: SSLEngine?) = if(type?.contains(cert.publicKey.algorithm)==true) ALIAS else null
            override fun getServerAliases(type: String?, issuers: Array<out Principal>?) = if(type?.contains(cert.publicKey.algorithm)==true) arrayOf(ALIAS) else null
            override fun chooseClientAlias(types: Array<out String>?, issuers: Array<out Principal>?, socket: java.net.Socket?): String? = null
            override fun getClientAliases(type: String?, issuers: Array<out Principal>?): Array<String>? = null
            override fun getCertificateChain(alias: String?) = arrayOf(cert)
            override fun getPrivateKey(alias: String?) = key
        }
        return CameraIdentity(SSLContext.getInstance("TLS").apply { init(arrayOf(manager),null,SecureRandom()) },cert)
    }
}
