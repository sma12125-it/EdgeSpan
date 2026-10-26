plugins {
    id("org.jetbrains.kotlin.jvm")
}

// The app-stream half (virtual display + MediaCodec) compiles against the platform stubs; app_process
// provides the real classes at runtime. JVM unit tests never load those classes.
val androidJar = (System.getenv("ANDROID_HOME") ?: System.getenv("ANDROID_SDK_ROOT") ?: "") + "/platforms/android-34/android.jar"

dependencies {
    implementation(project(":protocol"))
    compileOnly(files(androidJar))
    testImplementation(kotlin("test"))
}

java {
    sourceCompatibility = JavaVersion.VERSION_17
    targetCompatibility = JavaVersion.VERSION_17
}

kotlin {
    compilerOptions {
        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
    }
}
