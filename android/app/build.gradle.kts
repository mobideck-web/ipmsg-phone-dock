plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}
android {
    namespace = "app.lantern.lan"
    compileSdk = 35
    defaultConfig { applicationId = "app.lantern.lan"; minSdk = 29; targetSdk = 35; versionCode = 15; versionName = "0.8.1"; testInstrumentationRunner = providers.gradleProperty("ipmsgRunner").getOrElse("androidx.test.runner.AndroidJUnitRunner") }
    defaultConfig { testProguardFiles("test-proguard-rules.pro") }
    packaging { resources { excludes += setOf("META-INF/versions/**/OSGI-INF/MANIFEST.MF", "META-INF/versions/**/module-info.class", "META-INF/*.SF", "META-INF/*.DSA", "META-INF/*.RSA"); merges += "META-INF/LICENSE*" } }
    buildFeatures { compose = true }
    compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
    kotlinOptions { jvmTarget = "17" }
    testOptions { unitTests.isReturnDefaultValues = true }
    testBuildType = providers.gradleProperty("ipmsgTestBuildType").getOrElse("debug")
    buildTypes {
        getByName("release") {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }
}
dependencies {
    implementation("org.bouncycastle:bcpkix-jdk18on:1.86")
    implementation(platform("androidx.compose:compose-bom:2025.04.01"))
    implementation("androidx.activity:activity-compose:1.10.1")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.ui:ui-tooling-preview")
    debugImplementation("androidx.compose.ui:ui-tooling")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.9.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")
    testImplementation("junit:junit:4.13.2")
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
    androidTestImplementation("androidx.test:runner:1.6.2")
    androidTestImplementation("androidx.test:core:1.6.1")
}
