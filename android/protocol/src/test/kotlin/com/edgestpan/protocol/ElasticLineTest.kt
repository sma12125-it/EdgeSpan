package com.edgestpan.protocol

import kotlin.test.Test
import kotlin.test.assertTrue

class ElasticLineTest {
    @Test
    fun deformationPeaksAtCrossingPoint() {
        val cross = 0.3
        val at = ElasticLine.displacement(ElasticLine.ENTER, cross, cross, 150.0)
        val off = ElasticLine.displacement(ElasticLine.ENTER, 0.6, cross, 150.0)
        assertTrue(at > 0.5 && off < 0.01)
        assertTrue(ElasticLine.displacement(ElasticLine.EXIT, cross, cross, 80.0) < -0.3)
    }

    @Test
    fun enterTearsAtCrossingThenFades() {
        assertTrue(ElasticLine.opacity(ElasticLine.ENTER, 0.5, 0.5, 300.0) < 0.05)
        assertTrue(ElasticLine.opacity(ElasticLine.ENTER, 0.9, 0.5, 300.0) > 0.2)
        assertTrue(ElasticLine.opacity(ElasticLine.ENTER, 0.9, 0.5, 470.0) == 0.0)
    }
}
