# Keep app entrypoints/model names stable for OS restoration and release interoperability tests.
-keep class app.lantern.lan.** { *; }
-dontwarn javax.naming.**
-dontwarn javax.mail.**
-dontwarn javax.activation.**
-dontwarn org.bouncycastle.jsse.**
